using System;

namespace Jure
{
    // This code implements the remote capabilities for the famous Junkers Morse Key attached to a Serial port, pin6
    // (c) 2020, Gottfried Rudorfer, OE3GRJ
    // This code is for use inside a project created with the Visual C# > Windows Desktop > Console Application template.
    // Replace the code in Program.cs with this code.

    using System;
    using System.IO.Ports;
    using System.Threading;
    using System.Collections.Generic;
    using NAudio.Wave;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Runtime.InteropServices;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;


    // we use a special data structure in the protocol sent between the key and the rig
    public struct msgHeader
    {
        public byte clientCounter; // a reference number created by the client (on the key side) stored in a byte (0..255)
        public byte bitVector; // a bitVector of 8 bits showing the status of pin6 queried in the _waitTime interval (10 ms)
        public byte serverCounter; // a reference number created by the server (on the rig side) stored in a byte (0..255)
    }

    // Interop facilities to convert between ByteArrays and Structs
    class InterOp
    {
        public static object ByteArrayToStruct(byte[] array, int offset, Type structType)
        {
            if (array == null)
                throw new ArgumentNullException("array");

            if (structType == null)
                throw new ArgumentNullException("structType");

            if (!structType.IsValueType)
                throw new ArgumentException("structType");

            int size = Marshal.SizeOf(structType);

            if (array.Length < size)
                throw new ArgumentException("array");

            if ((offset < 0) || (offset + size > array.Length))
                throw new ArgumentOutOfRangeException("offset");

            object structure;
            GCHandle arrayHandle = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = arrayHandle.AddrOfPinnedObject();
                ptr += offset;
                structure = Marshal.PtrToStructure(arrayHandle.AddrOfPinnedObject(), structType);
            }
            finally
            {
                arrayHandle.Free();
            }
            return structure;
        }

        public static byte[] StructToByteArray(object structure)
        {
            if (structure == null)
                throw new ArgumentNullException("structure");

            Type structType = structure.GetType();

            if (!structType.IsValueType)
                throw new ArgumentException("structure");

            int size = Marshal.SizeOf(structType);

            byte[] array = new byte[size];
            GCHandle arrayHandle = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = arrayHandle.AddrOfPinnedObject();
                Marshal.StructureToPtr(structure, ptr, false);
            }
            finally
            {
                arrayHandle.Free();
            }
            return array;
        }
    }

    // the server part of the program, which will run on the rig side
    class Server
    {

        private static List<int> lPings = new List<int>();
        private static int oldAvg = 0;
        private static List<msgHeader> lMQ = new List<msgHeader>();
        private static List<msgHeader> lNQ = new List<msgHeader>();
        private static int avgPing = 0;
        private static SerialPort _serialPort = new SerialPort();
        private static int waitMilliSecs = 10;
        private static bool _continue = false;
        private static WaveOutEvent waveOut;
        private static long startTime;
        private static Dictionary<int, long> dServerTime = new Dictionary<int, long>();
        private static List<int> lCliNos = new List<int>();
        private static bool bStartAudio = false;

        // function to store the last 10 roundtrip times and calculate an average ping value
        public static int checkPing(int iPing)
        {
            lPings.Add(iPing);
            if (lPings.Count > 10)
                lPings.RemoveAt(0);
            int sum = 0;
            for (int i = 0; i < lPings.Count; i++)
            {
                sum += lPings[i];
            }

            if (lPings.Count < 10)
                return 0;

            int avg = sum / lPings.Count;

            if (avg == 0)
                avg = 1;

            avgPing = avg;

            // print the ping value if it changes by 10%
            if (Math.Abs(oldAvg - avg) / avg > 0.1)
            {
                Console.Write(avg + " ms ");
                oldAvg = avg;
            }

            return avg;
        }

        // Display available IPv4 IP addresses, this is useful to know the IP address the server shall bind to
        public static void listIPadresses()
        {
            // Get host name
            String strHostName = Dns.GetHostName();

            // Find host by name
            IPHostEntry iphostentry = Dns.GetHostEntry(strHostName);

            Console.WriteLine("\nAvailable IP address(es) for " + strHostName + ":");
            // Enumerate IP addresses
            foreach (IPAddress ipaddress in iphostentry.AddressList)
            {
                Regex IP4Regex = new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$");
                if (IP4Regex.IsMatch(ipaddress.ToString()))
                {
                    Console.WriteLine("   " + ipaddress);
                }
            }
        }

        // we send the dit and dahs via the serial port, pin7 to the rig
        // between the rig and the serial port there is an opto coppler to have galvanic separation of the serial port and the rig
        public static void SerialOut()
        {
            Console.WriteLine("Thread SerialOut");

            bool bLastPin7 = false;
            bool bPin7 = false;
            bool bLast8AllZero = true;

            while (_continue)
            {
                long ms1 = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                // we take over the data to be sent from the list lMQ into our private list lNQ
                lock (lMQ)
                {
                    lock (lNQ)
                    {
                        lNQ.AddRange(lMQ);
                        lMQ.Clear();
                    }
                }

                if (lNQ.Count == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                //PortChat.Log2("Before 1" + lNQ.Count);

                // we are delaying the replay to wipe out small delays on the network between key and rig
                // in case that the bitVector is zero, and the queue is smaller than 5, we delay a bit
                bLast8AllZero = lNQ[lNQ.Count - 1].bitVector > 0;
                if (bLast8AllZero && lNQ.Count < 5)
                {
                    Thread.Sleep(10);
                    continue;
                }

                //PortChat.Log2("Before 2" + lNQ.Count);

                msgHeader aHs = lNQ[0];
                //PortChat.Log2("S1: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " + lNQ.Count);

                // the bitVector contains 8 bits which we replay in this loop to the output pin, pin7
                for (int i = 0; i < 8; i++)
                {
                    bPin7 = (aHs.bitVector & (1 << i)) != 0;

                    //Console.WriteLine(aHs.clientTimeStamp + " " + lNQ.Count + " " + bPin7 );

                    if (!bLastPin7 && bPin7)
                    {
                        _serialPort.RtsEnable = true;
                        bLastPin7 = bPin7;
                        if (bStartAudio)
                            waveOut.Play();
                    }
                    else if (bLastPin7 && !bPin7)
                    {
                        if (bStartAudio)
                            waveOut.Stop();
                        _serialPort.RtsEnable = false;
                        bLastPin7 = bPin7;
                    }

                    //PortChat.Log2("S2: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " + i + " " + aHs.clientCounter + " " + bPin7 );

                    Thread.Sleep(waitMilliSecs);

                    /* Using Pin 5 = Ground and Pin 7 RTS out to generate +5, +-0 V output
                        * GR 2020-07-19 tested okay */
                }
                lock (lNQ)
                {
                    lNQ.RemoveAt(0);
                }
            }
            _serialPort.RtsEnable = false;
        }

        // we do not directly send to the serial port, instead we queue it in list lMQ
        public static bool SerialSend(msgHeader msg)
        {
            int iNQel = lNQ.Count;
            if (iNQel > 10000)
                return false;
            else
                lock (lMQ)
                    lMQ.Add(msg);

            return true;
        }

        // this implements the server/rig part
        public static void Run(string SERVER_IP, int PORT_NO, int iCom, WaveOutEvent wo, bool so)
        {
            waveOut = wo;
            bStartAudio = so;

            List<msgHeader> lCliMsg = new List<msgHeader>();

            _serialPort.PortName = "com" + iCom;
            _serialPort.DtrEnable = true; // GR needed
            _serialPort.Open();

            _continue = true;
            Thread serialThread = new Thread(SerialOut);

            Console.WriteLine("Starting thread SerialOut");
            serialThread.Start();

        MyStart:
            oldAvg = 0;
            startTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            //---listen at the specified IP and port no.---
            IPAddress localAdd = IPAddress.Parse(SERVER_IP);
            TcpListener listener = new TcpListener(localAdd, PORT_NO);
            Console.WriteLine("Listening on ip=" + SERVER_IP + ", port=" + PORT_NO + " ...");
            listener.Start();

            //---incoming client connected---
            TcpClient client = listener.AcceptTcpClient();

            //---get the incoming data through a network stream---
            NetworkStream nwStream = client.GetStream();
            byte[] buffer = new byte[client.ReceiveBufferSize];
            int iSrvCounter = 0;

            while (_continue)
            {
                try
                {
                    //---read incoming stream--- which might contain multiple packets in one
                    int bytesRead = nwStream.Read(buffer, 0, client.ReceiveBufferSize);

                    Dictionary<int, msgHeader> dMH = new Dictionary<int, msgHeader>();

                    // the read data may contain multiple msgHeader elements, we process all of them in this loop
                    for (int j = 0; j < bytesRead; j += 3)
                    {
                        byte[] bufInstance = buffer.Skip(j).Take(3).ToArray();
                        msgHeader aH = (msgHeader)InterOp.ByteArrayToStruct(bufInstance, 0, typeof(msgHeader));

                        PortChat.Log2("I: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " index unsorted=" + aH.clientCounter);

                        dMH[aH.clientCounter] = aH;
                    }

                    int firstindex = -1;
                    int index = 0;
                    // find the newest client packet, as we reset the counter after 255, need to do a search backwards
                    foreach (KeyValuePair<int, msgHeader> elem in dMH)
                    {
                        index = elem.Key;
                        do
                        {
                            if ((firstindex < 0) || (firstindex > index))
                                firstindex = index;

                            PortChat.Log2("I: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " index=" + index + " firstindex=" + firstindex);

                            index--;
                            if (index < 0)
                                index = 255;
                        } while (dMH.ContainsKey(index));
                    }

                    index = firstindex;
                    PortChat.Log2("I: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " firstindex=" + firstindex);

                    // now process all all msgHeader elements stored in the dictionary dMH
                    do
                    {
                        msgHeader aH = dMH[index];

                        long lNowMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                        PortChat.Log2("Server read: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " +
                            bytesRead + " clientCounter=" + aH.clientCounter + " bitvector=" + Convert.ToString(aH.bitVector, 2).PadLeft(8, '0') +
                            " serverCounter=" + aH.serverCounter);

                        //Console.WriteLine(aH.clientTimeStamp + " " + aH.cwState);

                        // we sent to the client the serverCounter, which we now get back
                        // we can now calulate a roundtrip time seen on the server side
                        if (dServerTime.ContainsKey(aH.serverCounter))
                        {
                            //PortChat.Log2("Before 3" + aH.serverCounter);

                            long srvMs = dServerTime[aH.serverCounter];
                            int iPing = Convert.ToInt32(lNowMs - srvMs);
                            checkPing(iPing);
                            //Console.Write(iPing + "ms ");
                            dServerTime.Remove(aH.serverCounter);
                        }

                        // add a new server reference number and store the milliseconds since epoch in the dictionary dServerTime
                        aH.serverCounter = Convert.ToByte(iSrvCounter);
                        dServerTime[iSrvCounter] = lNowMs;

                        PortChat.Log2("Server send: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " clientCounter=" +
                            aH.clientCounter + " bitvector=" + Convert.ToString(aH.bitVector, 2).PadLeft(8, '0') + " serverCounter=" + aH.serverCounter);


                        byte[] bytesToSend = InterOp.StructToByteArray(aH);
                        //---write back the text to the client---
                        // Console.WriteLine("Sending back : " + dataReceived);
                        nwStream.Write(bytesToSend, 0, bytesToSend.Length);

                        // we add the data to be send to the serial prot to the queue
                        SerialSend(aH);

                        index++;
                        if (index > 255)
                            index = 0;
                    } while (dMH.ContainsKey(index));

                    Thread.Sleep(waitMilliSecs);

                    if (iSrvCounter == 255)
                        iSrvCounter = 0;
                    else
                        iSrvCounter++;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Server exception: " + e);
                    client.Close();
                    listener.Stop();
                    waveOut.Stop();
                    goto MyStart;
                }
            }
            _continue = false;
            serialThread.Join();
            _serialPort.Close();
        }

    }

    // Wave provide implementation from 
    // https://markheath.net/post/playback-of-sine-wave-in-naudio
    public abstract class WaveProvider32 : IWaveProvider
    {
        private WaveFormat waveFormat;

        public WaveProvider32()
            : this(44100, 1)
        {
        }

        public WaveProvider32(int sampleRate, int channels)
        {
            SetWaveFormat(sampleRate, channels);
        }

        public void SetWaveFormat(int sampleRate, int channels)
        {
            this.waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            WaveBuffer waveBuffer = new WaveBuffer(buffer);
            int samplesRequired = count / 4;
            int samplesRead = Read(waveBuffer.FloatBuffer, offset / 4, samplesRequired);
            return samplesRead * 4;
        }

        public abstract int Read(float[] buffer, int offset, int sampleCount);

        public WaveFormat WaveFormat
        {
            get { return waveFormat; }
        }
    }

    // Sine Ware Provider from
    // https://markheath.net/post/playback-of-sine-wave-in-naudio
    public class SineWaveProvider32 : WaveProvider32
    {
        int sample;

        public SineWaveProvider32()
        {
            Frequency = 1000;
            Amplitude = 0.25f; // let's not hurt our ears
        }

        public float Frequency { get; set; }
        public float Amplitude { get; set; }

        public override int Read(float[] buffer, int offset, int sampleCount)
        {
            int sampleRate = WaveFormat.SampleRate;
            for (int n = 0; n < sampleCount; n++)
            {
                buffer[n + offset] = (float)(Amplitude * Math.Sin((2 * Math.PI * sample * Frequency) / sampleRate));
                sample++;
                if (sample >= sampleRate) sample = 0;
            }
            return sampleCount;
        }
    }

    // dictinary defining some morse alphabet
    public class MorseAlphabet
    {
        public static Dictionary<string, char> morseDict = new Dictionary<string, char>()
    {
       {".-", 'a'}, {"-...", 'b'}, {"-.-.", 'c'}, {"-..", 'd'}, {".", 'e'}, {"..-.", 'f'},
       {"--.", 'g'}, {"....", 'h'}, {"..", 'i'}, {".---", 'j'}, {"-.-", 'k'}, {".-..", 'l'},
       {"--", 'm'}, {"-.", 'n'}, {"---", 'o'}, {".--.", 'p'}, {"--.-", 'q'}, {".-.", 'r'},
        {"...", 's'}, {"-", 't'}, {"..-", 'u'}, {"...-", 'v'}, {".--", 'w'}, {"-..-", 'x'},
        {"-.--", 'y'}, {"--..", 'z'}, {"-----", '0'}, {".----", '1'}, {"..---", '2'}, {"...--", '3'},
        {"....-", '4'}, {".....", '5'}, {"-....", '6'}, {"--...", '7'}, {"---..", '8'}, {"----.", '9'},
        {"-...-", '='}, {".-.-.", '+'}, {".-.-.-", '.'}, {"-..-.", '/'}, {"..--..", '?'}, {"--..--", ','},
        {"-....-", '-'}, {".-.-", 'ä'}, {"---.", 'ö'}, {"..--", 'ü'}, {"...--...", 'ß'}
   };
    }

    // Simple emperical approach to distinguish dits from dahs, we use an array of the past 50 durations the key was on
    //  and split into 2 groups, calculate the variance of both groups
    //  the best fit is when the total variance is the minumum
    public class mClust
    {
        public string myField = string.Empty;

        public mClust()
        {

        }

        public void AddArr(long value)
        {
            // Implement a buffer of the last onArrSize elements
            if (onArr.Count < onArrSize)
                onArr.Add(value);
            else
            {
                onArr.RemoveAt(0);
                onArr.Add(value);
            }
        }

        public double Mean(long[] lArr)
        {
            double lMean = 0;
            for (int i = 0; i < lArr.Length; i++)
            {
                lMean += lArr[i];
            }
            return lMean / lArr.Length;
        }

        public double Variance(long[] lArr)
        {
            double mean = Mean(lArr);
            double var = 0;
            for (int i = 0; i < lArr.Length; i++)
            {
                var += Math.Pow(lArr[i] - mean, 2.0);
            }
            return var / lArr.Length;
        }

        public double getBestThres()
        {
            bestThres = 0;
            double minvs = Double.PositiveInfinity;

            for (int i = 10; i < 1000; i += 10)
            {
                List<long> shorts = new List<long>();
                List<long> longs = new List<long>();

                for (int j = 0; j < onArr.Count; j++)
                {
                    if (onArr[j] <= i)
                    {
                        shorts.Add(onArr[j]);

                    }
                    else
                    {
                        longs.Add(onArr[j]);
                    }
                }

                if ((shorts.Count < 4) || (shorts.Count < 4))
                    continue;

                double vs = Variance(shorts.ToArray());
                double vl = Variance(longs.ToArray());
                double totalv = vs + vl;

                if (totalv < minvs)
                {
                    minvs = totalv;
                    bestThres = i;
                }
            }

            return bestThres;
        }

        private static int onArrSize = 50;
        private List<long> onArr = new List<long>();
        private double bestThres;
    }

    // the main starting point of JuRe as well as the implementation on the key side
    public class PortChat
    {
        public const string _sVersion = "v2020.08.04.01";
        private static bool _bHelp = false;
        static bool _continue;
        static SerialPort _serialPort;
        private static WaveOutEvent waveOut = new WaveOutEvent();
        private static int iAudioDevice = 0;
        private static int iFrequency = 700;
        private static int iCom = 1;
        private static bool bRig = false;
        private static bool bKey = false;
        private static string sIP = "127.0.0.1";
        private static int iPort = 7373;
        private static bool bSideSwiper = false;
        private static List<msgHeader> lMQ = new List<msgHeader>();
        private static List<msgHeader> lNQ = new List<msgHeader>();
        private static int _waitMilliSecs = 10;
        private static long startTime;
        private static int avgPing = 0;
        private static FileStream _logStream = null;
        private static string _logFile;
        public static StreamWriter _logStrWr = null;
        // The response from the remote device.  
        private static String response = String.Empty;
        private static ManualResetEvent receiveDone = new ManualResetEvent(false);
        private static Dictionary<int, long> dCliTimeStamps = new Dictionary<int, long>();
        private static List<int> lSrvNos = new List<int>();

        // init wave provider, we are using Sine Waves
        static void MyInitNAudio()
        {
            var sineWaveProvider = new SineWaveProvider32();
            sineWaveProvider.SetWaveFormat(16000, 1); // 16kHz mono
            sineWaveProvider.Frequency = iFrequency;
            sineWaveProvider.Amplitude = 0.25f;
            waveOut.DeviceNumber = iAudioDevice;
            waveOut.Init(sineWaveProvider);
        }

        // Display available Audio Devices
        public static void printAudioDevices()
        {
            Console.WriteLine("\nAvailable Audio Device(s):");

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                WaveOutCapabilities WOC = WaveOut.GetCapabilities(i);
                Console.WriteLine("   " + i + " = " + WOC.ProductName);
            }
        }

        // Display available Serial Ports 
        public static void printComPortNames()
        {
            Console.WriteLine("\nAvailable Serial Port(s):");
            foreach (string s in SerialPort.GetPortNames())
            {
                Console.WriteLine("   {0}", s);
            }
        }


        // tried file locked logging, however the it turned out that locking caused timing issues in the whole program
        public static void Log(string logMessage)
        {
            string sMsg = String.Format("{0}: {1}\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), logMessage);
            if (_logStream == null)
            {
                _logStream = new FileStream(_logFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }

            UnicodeEncoding uniEncoding = new UnicodeEncoding();

            lock (_logStream)
            {
                _logStream.Seek(0, SeekOrigin.End);
                // _logStream.Lock(0, 1);


                _logStream.Write(uniEncoding.GetBytes(sMsg), 0, uniEncoding.GetByteCount(sMsg));
                // _logStream.Unlock(0, 1);
                _logStream.Flush();
            }
        }

        // file logging function, to have traceability of code execution
        public static void Log2(string logMessage)
        {
            if (_logStrWr == null)
            {
                _logStrWr = File.AppendText(_logFile);
            }

            _logStrWr.Write("\r\n");
            _logStrWr.Write("{0}: {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), logMessage);
            _logStrWr.Flush();
        }

        public static void Main(string[] args)
        {
            bool bStartAudio = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-audiodevice")
                {
                    iAudioDevice = Convert.ToInt32(args[++i]);
                    bStartAudio = true;
                }
                else if (args[i] == "-freq")
                    iFrequency = Convert.ToInt32(args[++i]);
                else if (args[i] == "-com")
                    iCom = Convert.ToInt32(args[++i]);
                else if (args[i] == "-rig")
                    bRig = true;
                else if (args[i] == "-key")
                    bKey = true;
                else if (args[i] == "-ip")
                    sIP = args[++i];
                else if (args[i] == "-sideswiper")
                    bSideSwiper = true;
                else if (args[i] == "-port")
                    iPort = Convert.ToInt32(args[++i]);
                else if (args[i] == "-version")
                {
                    System.Console.WriteLine(System.AppDomain.CurrentDomain.FriendlyName + " Version: " + _sVersion);
                    Environment.Exit(1);
                }
                else if (args[i] == "-help")
                    _bHelp = true;
                else
                {
                    System.Console.WriteLine("Invalid arg \"" + args[i] + "\"");
                    Environment.Exit(1);
                }
            }

            if (args.Length == 0 || _bHelp)
            {
                System.Console.WriteLine(System.AppDomain.CurrentDomain.FriendlyName + " Version: " + _sVersion);
                printAudioDevices();
                printComPortNames();
                Server.listIPadresses();
                System.Console.WriteLine("\nUsage on the rig side: " + System.AppDomain.CurrentDomain.FriendlyName + " -rig -com <no> -ip <ip> -port <port>");
                System.Console.WriteLine("Usage on the key side: " + System.AppDomain.CurrentDomain.FriendlyName + " -key -com <no> -audiodevice <no> -freq <freq> -ip <rig ip> -port <rig port> \n");
                System.Console.WriteLine("Addional options: " + System.AppDomain.CurrentDomain.FriendlyName + " -version ... display version information   -help ... display help information\n");
            }

            if (_bHelp)
            {
                System.Console.WriteLine(System.AppDomain.CurrentDomain.FriendlyName + " is a tool which allows to run the Junkers Key over a TCP socket.");
                System.Console.WriteLine("You need two windows x64 computers with Serial RS232 interfaces, as most modern computers are lacking this kind of interface,\n" +
                   "use a USB to Serial Adapter like the Digitus DA-70156 on each computer.\n" +
                   "On the Rig side a custom cable between the RS232 and the Rig-CW plug containing an optocoupler is needed.\n" +
                   "On the key side a different custom cable is needed to be able to connect the Junkers straight key\n\n" +
                   "Additional command line options: -key -sideswiper ...  also read beside pin6, pin8\n" + 
                   "\t on the rig side you may also add -audiodevice <device> if you want to hear a listening tone on the rig computer\n" +
                   "This application requires .NET Core Runtime 3.1, download available at https://dotnet.microsoft.com/download/dotnet-core/3.1" );
            }

            if (args.Length == 0 || _bHelp)
                Environment.Exit(1);

            Process myProcess = Process.GetCurrentProcess();
            myProcess.PriorityClass = ProcessPriorityClass.RealTime;
            myProcess.PriorityBoostEnabled = true;
            if ((myProcess.PriorityClass != ProcessPriorityClass.RealTime) || (myProcess.PriorityBoostEnabled == false))
            {
                Console.WriteLine("Cannot change priority to RealTime. Did you start me as Administrator?");
                Environment.Exit(1);
            }
            string sPrio;
            if (myProcess.BasePriority >= 24)
                sPrio = "RealTime";
            else if (myProcess.BasePriority >= 13)
                sPrio = "High";
            else
                sPrio = "Normal";

            if (sPrio != "RealTime")
                Console.WriteLine("Process is running at priority " + sPrio + ", for Real Time it needs to run as Administrator.");
            else
                Console.WriteLine("Process is running at priority " + sPrio + ". This is the highest possible priority.");

            if ((bRig && bKey) || (!bRig && !bKey))
            {
                System.Console.Write("You have to specify -rig or -key");
                Environment.Exit(1);
            }

            // initialize audio
            MyInitNAudio();

            // in case of the rig option, this will start the rig part
            if (bRig)
            {

                _logFile = "log-" + DateTime.Now.ToString("yyyy-MM-dd") + "-Srv.txt";

                startTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                Log2("Startup of Server");

                Server.Run(sIP, iPort, iCom, waveOut, bStartAudio);
                Environment.Exit(0);
            }

            // System.Console.WriteLine("Size=" + Marshal.SizeOf(typeof(msgHeader)));

            _logFile = "log-" + DateTime.Now.ToString("yyyy-MM-dd") + "-Cli.txt";
            startTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            Log2("Startup of Client");

            string message;
            StringComparer stringComparer = StringComparer.OrdinalIgnoreCase;
            Thread readThread = new Thread(Read); // we read from the rig with an own thread
            Thread sendThread = new Thread(NetSendThread); // we write to the rig with an own thread

            // Create a new SerialPort object with default settings.
            _serialPort = new SerialPort();

            // Set the port name
            _serialPort.PortName = "com" + iCom;

            // this is needed for reading pin6 and pin8
            _serialPort.DtrEnable = true;

            _serialPort.Open();
            _continue = true;
            readThread.Start();
            sendThread.Start();

            Console.WriteLine("Type QUIT to exit");

            while (_continue)
            {
                message = Console.ReadLine();

                if (stringComparer.Equals("quit", message))
                {
                    _continue = false;
                }
                else
                {
                    // Read();
                }
            }

            readThread.Join();
            sendThread.Join();
            _serialPort.Close();

            waveOut.Dispose();
            waveOut = null;
        }

        // State object for reading client data asynchronously  
        public class StateObject
        {
            // Client  socket.  
            public Socket workSocket = null;
            // Size of receive buffer.  
            public const int BufferSize = 3;
            // Receive buffer.  
            public byte[] buffer = new byte[BufferSize];
            // Receiving buffer
            public List<byte[]> bReceived = new List<byte[]>();
        }
        private static void Receive(Socket client)
        {
            // Create the state object.  
            StateObject state = new StateObject();
            state.workSocket = client;

            // Begin receiving the data from the remote device.  
            client.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                new AsyncCallback(ReceiveCallback), state);
        }

        private static void ReceiveCallback(IAsyncResult ar)
        {
            // Retrieve the state object and the client socket   
            // from the asynchronous state object.  
            StateObject state = (StateObject)ar.AsyncState;
            Socket client = state.workSocket;

            // Read data from the remote device.  
            int bytesRead = client.EndReceive(ar);

            // Log2("Client ReceiveCallback bytesRead=" + bytesRead);

            if (bytesRead > 0)
            {
                // There might be more data, so store the data received so far.  
                // state.sb.Append(Encoding.ASCII.GetString(state.buffer, 0, bytesRead));
                state.bReceived.Add(state.buffer.ToArray());

                // Get the rest of the data.  
                client.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                    new AsyncCallback(ReceiveCallback), state);
            }
            else
            {
                // Signal that all bytes have been received.  
                receiveDone.Set();
            }

            if (state.bReceived.Count > 0)
            {
                for (int j = 0; j < state.bReceived.Count; j += 3)
                {
                    byte[] bufInstance = state.bReceived[0];
                    msgHeader aHr = (msgHeader)InterOp.ByteArrayToStruct(bufInstance, 0, typeof(msgHeader));

                    Log2("Client receive:" + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " c=" + aHr.clientCounter + " s=" + aHr.serverCounter + " bytesRead=" + bytesRead);

                    lSrvNos.Add(aHr.serverCounter);

                    if (dCliTimeStamps.ContainsKey(aHr.clientCounter))
                    {

                        long ms = dCliTimeStamps[aHr.clientCounter];
                        dCliTimeStamps.Remove(aHr.clientCounter);

                        int iPing = Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - ms);
                        avgPing = Server.checkPing(iPing);
                        //Console.Write(iPing + "ms ");
                    }
                }
                state.bReceived.Clear();
            }

        }


        public static void NetSendThread()
        {
            //---create a TCPClient object at the IP and port no.---
            TcpClient client = new TcpClient(sIP, iPort);
            NetworkStream nwStream = client.GetStream();

            while (_continue)
            {
                long ms1 = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                // we take over the data to send from the list lMQ into our privat list lNQ
                lock (lMQ)
                {
                    lock (lNQ)
                        lNQ.AddRange(lMQ);
                    lMQ.Clear();
                }

                while (lNQ.Count > 0)
                {
                    msgHeader aHs = lNQ[0];

                    byte[] bytesToSend = InterOp.StructToByteArray(aHs);
                    lNQ.RemoveAt(0);

                    Log2("Client send: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " + bytesToSend.Length + " clientCounter=" + aHs.clientCounter + " bitVector=" + Convert.ToString(aHs.bitVector, 2).PadLeft(8, '0') + " serverCounter=" + aHs.serverCounter);
                    nwStream.Write(bytesToSend, 0, bytesToSend.Length);

                    Receive(client.Client);
                }

                long ms2 = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                int delta = Convert.ToInt32(ms2 - ms1);
                int s;
                if (delta > _waitMilliSecs)
                    s = 0;
                else
                    s = _waitMilliSecs - delta;

                Thread.Sleep(s);
            }
            client.Close();
        }

        // we send a struct by adding it to the public list lMQ
        public static bool NetSend(msgHeader msg)
        {
            int iNQel = lNQ.Count;
            if (iNQel > 10)
                return false;
            else
                lock (lMQ)
                    lMQ.Add(msg);

            return true;
        }

        // loop for polling the serial pins where our Junkers key is attached
        // polling is every _waitMilliSecs, currently 10ms
        public static void Read()
        {
            bool pin6_last = false;
            bool pin8_last = false;
            long oldONmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            long oldOFFmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            mClust amClust = new mClust();
            mClust offClust = new mClust();

            double bestOn = 0;
            double bestOff = 0;
            List<char> mChar = new List<char>();
            MorseAlphabet mDict = new MorseAlphabet();

            int iScanCount = 0;
            int iClientCount = 0;
            byte bOnOffStates = 0;

            while (_continue)
            {
                try
                {
                    // query relevant pins on serial port
                    bool pin6 = _serialPort.DsrHolding;
                    bool pin8 = _serialPort.CtsHolding;

                    if (bSideSwiper)
                        pin6 |= pin8; // hack add the status of Pin8 for SideSwiper

                    // the designed protocol uses a byte to store 8 polls
                    if (pin6)
                        bOnOffStates |= (byte)(1 << iScanCount);

                    long lMilliSecs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                    // when all bits of the byte are set, we send this over to the rig
                    if (iScanCount == 7)
                    {

                        // the implemented protocol uses one byte to store a reference created by the client
                        // in our case it has the size of a byte (0..255), the counter is reset to 0 when it hits the boundary
                        // with the client refrence (0..255) the epoch time in ms is kept in a dictinoary,
                        // the server will send the client reference unmodified back to the client, and the client can calculate a rounttrip time 
                        if (dCliTimeStamps.ContainsKey(iClientCount))
                        {
                            Log2("Warning: Client time stamp not cleared=" + iClientCount);
                            dCliTimeStamps.Remove(iClientCount);
                        }
                        dCliTimeStamps.Add(iClientCount, lMilliSecs);

                        msgHeader mHs;

                        mHs.bitVector = bOnOffStates;

                        // the client stores a list of reference numbers sent by the server
                        // when the client sends this back to the server, it removes it from the list as done
                        mHs.serverCounter = 0;
                        if (lSrvNos.Count > 0)
                        {
                            mHs.serverCounter = Convert.ToByte(lSrvNos[0]);
                            lSrvNos.RemoveAt(0);
                        }

                        mHs.clientCounter = Convert.ToByte(iClientCount);

                        // hand over the data stored in the msgHeader structure indirectly to the send thread
                        NetSend(mHs);
                        iScanCount = 0;
                        bOnOffStates = 0;

                        if (iClientCount == 255)
                            iClientCount = 0;
                        else
                            iClientCount++;

                    }
                    else
                        iScanCount++;

                    // capture the case where the last status of the key was up (0) and the new is down (1).
                    if (pin6 && !pin6_last)
                    {
                        //start the listening tone
                        waveOut.Play();
                        long deltaMs = lMilliSecs - oldOFFmilliseconds;
                        //Console.WriteLine(milliseconds + "-" + oldOFFmilliseconds);
                        // Console.Write(deltaMs + " ");

                        // we store off durations for furhter cluster processing
                        offClust.AddArr(deltaMs);

                        // we devide the off cluster in two groups, by searching for the lowest variance
                        bestOff = offClust.getBestThres();
                        if (bestOff > 0)
                        {
                            /*
                            Console.WriteLine("\n" + deltaMs + ":" + bestOn);
                            if (deltaMs > 2* bestOn * 0.8)
                                Console.Write(" * "); // Buchstabengrenze
                            else if (deltaMs > 3 * bestOn * 0.8) // ich mache mehr Pause --> Wortgrenze?
                                Console.Write(" | ");
                            */
                        }

                        oldONmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                    }

                    // now capture the case where the last status of the key was down (1) and the new is up (0).
                    if (!pin6 && pin6_last)
                    {
                        // stop the listening tone
                        waveOut.Stop();
                        long deltaMs = lMilliSecs - oldONmilliseconds;
                        // Console.Write(deltaMs + " ");

                        // add the on time to a cluster of on times
                        amClust.AddArr(deltaMs);

                        // devide into 2 clusters by minimizing the total variance of both clusters
                        bestOn = amClust.getBestThres();
                        if (bestOn > 0)
                        {
                            // we can now determine if this is a dit or dah
                            char c;
                            if (deltaMs <= bestOn)
                                c = '.';
                            else
                                c = '-';

                            mChar.Add(c);
                            //Console.Write(c);
                        }

                        oldOFFmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                    }


                    if (pin8 && !pin8_last)
                    {
                        // needs to be implemented, implement keyer functionality 
                    }


                    if (!pin8 && pin8_last)
                    {
                        // needs to be implemented, implement keyer functionality 
                    }

                    pin6_last = pin6;
                    pin8_last = pin8;

                    if (!pin6)
                    {
                        int wordm = 10;
                        long now = lMilliSecs - oldONmilliseconds;

                        // simple approach to detect if we it the character boundary
                        if ((now > 4 * bestOn) && (now < wordm * bestOn))
                        {
                            if (mChar.Count > 0)
                            {
                                string sMchar = new string(mChar.ToArray());


                                char fChar = '~';

                                // lookup for the corresponding letter in the morse dict
                                if (MorseAlphabet.morseDict.ContainsKey(sMchar))
                                {
                                    fChar = MorseAlphabet.morseDict[sMchar];
                                }

                                Console.WriteLine(" " + fChar.ToString().ToUpper() + sMchar.PadLeft(6));
                                mChar.Clear();
                                oldONmilliseconds = now;
                            }
                        }
                    }
                    //Log2("R:" + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " +pin6);

                }
                catch (TimeoutException) { }

                Thread.Sleep(_waitMilliSecs);
            }
        }

    }
}
