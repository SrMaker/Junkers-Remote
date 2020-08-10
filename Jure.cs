namespace Jure
{
    // This code (Junkers remote, JURE) implements the remote capabilities for the famous Junkers Morse Key attached to a Serial port, pin6
    // Pin6 is sampled by this program and the state information transferred to the instance of JURE running rig side where it replays the key strokes to
    // Pin7 of a serial port which is attached via an optocoupler to the key input of the rig.
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
    public struct MsgHeader
    {
        public byte clientCounter; // a reference number created by the client (on the key side) stored in a byte (0..255)
        public byte bitVector; // a bitVector of 8 bits showing the status of pin6 queried in the _waitMilliSecs interval (10 ms)
        public byte serverCounter; // a reference number created by the server (on the rig side) stored in a byte (0..255)
    }

    public class Jure
    {
        protected static List<MsgHeader> lMQ = new List<MsgHeader>();
        protected static List<MsgHeader> lNQ = new List<MsgHeader>();

        protected static FileStream _logStream = null;
        protected static string _logFile;
        protected static StreamWriter _logStrWr = null;

        protected static List<int> lPings = new List<int>();
        protected static int oldAvg = 0;
        protected static int avgPing = 0;
        protected static SerialPort _serialPort = new SerialPort();
        protected static int _waitMilliSecs = 10;
        protected static bool _continue = false;
        protected static WaveOutEvent waveOut = new WaveOutEvent();
        protected static long startTime;

        protected static bool bDisableLogging = false;

        protected const string _sVersion = "v2020.08.10.01";

        protected static bool _bHelp = false;
        protected static int iAudioDevice = 0;
        protected static int iFrequency = 700;
        protected static int iCom = 1;
        protected static bool bRig = false;
        protected static bool bKey = false;
        protected static string sIP = "127.0.0.1";
        protected static int iPort = 7373;
        protected static bool bSideSwiper = false;
        protected static bool bStartAudio = false;

        public static void Main(string[] args)
        {
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
                else if (args[i] == "-disablelogging")
                    bDisableLogging = true;
                else if (args[i] == "-version")
                {
                    System.Console.WriteLine("{0} Version: {1}", System.AppDomain.CurrentDomain.FriendlyName, _sVersion);
                    Environment.Exit(1);
                }
                else if (args[i] == "-help")
                    _bHelp = true;
                else
                {
                    System.Console.WriteLine("Invalid arg \"{0}\"", args[i]);
                    Environment.Exit(1);
                }
            }

            if (args.Length == 0 || _bHelp)
            {
                System.Console.WriteLine("{0} Version: {1}", System.AppDomain.CurrentDomain.FriendlyName, _sVersion);
                PrintAudioDevices();
                PrintComPortNames();
                ListIPadresses();
                System.Console.WriteLine("\nUsage on the rig side: {0} -rig -com <no> -ip <ip> -port <port>", System.AppDomain.CurrentDomain.FriendlyName);
                System.Console.WriteLine("Usage on the key side: {0} -key -com <no> -audiodevice <no> -freq <freq> -ip <rig ip> -port <rig port> \n", System.AppDomain.CurrentDomain.FriendlyName);
                System.Console.WriteLine("Addional options: {0} -version ... display version information   -help ... display help information\n", System.AppDomain.CurrentDomain.FriendlyName);
            }

            if (_bHelp)
            {
                System.Console.WriteLine("{0} is a tool which allows to run the Junkers Key over a TCP socket.", System.AppDomain.CurrentDomain.FriendlyName);
                System.Console.WriteLine("You need two windows x64 computers with Serial RS232 interfaces, as most modern computers are lacking this kind of interface,\n" +
                   "use a USB to Serial Adapter like the Digitus DA-70156 on each computer.\n" +
                   "On the Rig side a custom cable between the RS232 and the Rig-CW plug containing an optocoupler is needed.\n" +
                   "On the key side a different custom cable is needed to be able to connect the Junkers straight key\n\n" +
                   "Additional command line options: -key -sideswiper ...  also read beside pin6, pin8\n" +
                   "\t on the rig side you may also add -audiodevice <device> if you want to hear a listening tone on the rig computer\n" +
                   "\t -disablelogging ... disables logging to file for debugging\n" +
                   "This application requires .NET Core Runtime 3.1, download available at https://dotnet.microsoft.com/download/dotnet-core/3.1");
            }

            if (args.Length == 0 || _bHelp)
                Environment.Exit(1);

            if ((bRig && bKey) || (!bRig && !bKey))
            {
                System.Console.Write("You have to specify -rig or -key");
                Environment.Exit(1);
            }

            // Set process priority
            ProcessStuff();

            // initialize audio
            InitNAudio();

            // in cse of the rig option, this will start the rig part
            if (bRig)
            { 
                _logFile = "log-" + DateTime.Now.ToString("yyyy-MM-dd") + "-Rig.txt";

                startTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                Log2("Startup of Jure for Rig");

                Rig.Run();
                Environment.Exit(0);
            } else if (bKey) {
                _logFile = "log-" + DateTime.Now.ToString("yyyy-MM-dd") + "-Key.txt";

                startTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                Log2("Startup of Jure for Key");

                Key.Run();
                Environment.Exit(0);
            }
            else {
                System.Console.Write("You have to specify -rig or -key");
                Environment.Exit(0);
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

        // function to store the last 10 roundtrip times and calculate an average ping value
        public static int CheckPing(int iPing)
        {
            lPings.Add(iPing);
            if (lPings.Count > 10)
                lPings.RemoveAt(0);

            int sum = 0;
            for (int i = 0; i < lPings.Count; i++)
                sum += lPings[i];

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
        public static void ListIPadresses()
        {
            // Get host name
            String strHostName = Dns.GetHostName();

            // Find host by name
            IPHostEntry iphostentry = Dns.GetHostEntry(strHostName);

            Console.WriteLine("\nAvailable IP address(es) for {0}:", strHostName);
            // Enumerate IP addresses
            foreach (IPAddress ipaddress in iphostentry.AddressList)
            {
                Regex IP4Regex = new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$");
                if (IP4Regex.IsMatch(ipaddress.ToString()))
                {
                    Console.WriteLine("   {0}", ipaddress);
                }
            }
        }

        // Display available Audio Devices
        public static void PrintAudioDevices()
        {
            Console.WriteLine("\nAvailable Audio Device(s):");

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                WaveOutCapabilities WOC = WaveOut.GetCapabilities(i);
                Console.WriteLine("   {0}={1}", i, WOC.ProductName);
            }
        }

        // Display available Serial Ports 
        public static void PrintComPortNames()
        {
            Console.WriteLine("\nAvailable Serial Port(s):");
            foreach (string s in SerialPort.GetPortNames())
            {
                Console.WriteLine("   {0}", s);
            }
        }

        // intitalize audio
        static void InitNAudio()
        {
            var sineWaveProvider = new SineWaveProvider32();
            sineWaveProvider.SetWaveFormat(16000, 1); // 16kHz mono
            sineWaveProvider.Frequency = iFrequency;
            sineWaveProvider.Amplitude = 0.25f;
            waveOut.DeviceNumber = iAudioDevice;
            waveOut.Init(sineWaveProvider);
        }

        // set process priority
        static void ProcessStuff()
        {
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
                Console.WriteLine("Process is running at priority {0}, for Real Time it needs to run as Administrator.", sPrio);
            else
                Console.WriteLine("Process is running at priority {0}. This is the highest possible priority.", sPrio);
        }
    }

    // the server part of the program, which will run on the rig side
    class Rig : Jure
    {
        private static readonly Dictionary<int, long> dServerTime = new Dictionary<int, long>();

        // we send the dit and dahs via the serial port, pin7 to the rig
        // between the rig and the serial port there is an optocoupler to have galvanic separation of the serial port and the rig
        public static void SerialOut()
        {
            Console.WriteLine("Thread SerialOut");

            bool bLastPin7 = false;
            bool bPin7;
            bool bLast8AllZero;

            while (_continue)
            {
                // we take over the data to be sent from the list lMQ into our private list lNQ
                lock (lMQ)
                {
                    lock (lNQ)
                    {
                        lNQ.AddRange(lMQ);
                        lMQ.Clear();
                    }
                }

                if (lNQ.Count == 0) {
                    Thread.Sleep(_waitMilliSecs);
                    continue;
                }

                // we are delaying the replay to wipe out small delays on the network between key and rig
                // in case that the bitVector is zero, and the queue is smaller than 5, we delay a bit
                bLast8AllZero = lNQ[lNQ.Count - 1].bitVector == 0;
                if (bLast8AllZero && lNQ.Count < 5) {
                    Thread.Sleep(_waitMilliSecs); // we will wait here additional 10ms
                    continue;
                }

                MsgHeader aHs = lNQ[0];

                // the bitVector contains 8 bits which we replay in this loop to the output pin, pin7
                for (int i = 0; i < 8; i++)
                {
                    bPin7 = (aHs.bitVector & (1 << i)) != 0;

                    if (!bLastPin7 && bPin7) {
                        _serialPort.RtsEnable = true;
                        
                        bLastPin7 = bPin7;
                        if (bStartAudio)
                            waveOut.Play();
                        Morse.ToOn();

                    } else if (bLastPin7 && !bPin7) {
                        _serialPort.RtsEnable = false; 
                        
                        if (bStartAudio)
                            waveOut.Stop();
                        
                        Morse.ToOff();

                        bLastPin7 = bPin7;
                    }

                    if (!bPin7) 
                        Morse.IsOff();

                    // we are shorting the standard delay to wipe out if the server queue gets too big
                    if (bLast8AllZero && lNQ.Count > 10)
                        Thread.Sleep(_waitMilliSecs/2);
                    else
                        Thread.Sleep(_waitMilliSecs);

                    /* Using Pin 5 = Ground and Pin 7 RTS out to generate +5, +-0 V output
                        * GR 2020-07-19 tested okay */
                }
                lock (lNQ)
                    lNQ.RemoveAt(0);
            }
            _serialPort.RtsEnable = false;
        }

        // we do not directly send to the serial port, instead we queue it in list lMQ
        public static bool SerialSend(MsgHeader msg)
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
        public static void Run()
        {
            List<MsgHeader> lCliMsg = new List<MsgHeader>();

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
            IPAddress localAdd = IPAddress.Parse(sIP);
            TcpListener listener = new TcpListener(localAdd, iPort);
            Console.WriteLine("Listening on ip={0}, port={1} ...", sIP, iPort);
            listener.Start();

            //---incoming client connected---
            TcpClient client = listener.AcceptTcpClient();
            Console.WriteLine("Client connected with ip={0}",  client.Client.RemoteEndPoint.ToString());


            //---get the incoming data through a network stream---
            NetworkStream nwStream = client.GetStream();
            byte[] buffer = new byte[client.ReceiveBufferSize];
            int iSrvCounter = 0;

            while (_continue)
            {
                try
                {
                    long lNowMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                    //---read incoming stream--- which might contain multiple packets in one
                    int bytesRead = nwStream.Read(buffer, 0, client.ReceiveBufferSize);

                    Dictionary<int, MsgHeader> dMH = new Dictionary<int, MsgHeader>();

                    // the read data may contain multiple msgHeader elements, we process all of them in this loop
                    for (int j = 0; j < bytesRead; j += 3) {
                        byte[] bufInstance = buffer.Skip(j).Take(3).ToArray();
                        MsgHeader aH = (MsgHeader)InterOp.ByteArrayToStruct(bufInstance, 0, typeof(MsgHeader));

                        if (!bDisableLogging)
                            Log2("I: " + Convert.ToInt32(lNowMs-startTime) + " index unsorted=" + aH.clientCounter);

                        dMH[aH.clientCounter] = aH;
                    }

                    int firstindex = -1;
                    int index = 0;
                    // find the newest client packet, as we reset the counter after 255, need to do a search backwards
                    foreach (KeyValuePair<int, MsgHeader> elem in dMH)
                    {
                        index = elem.Key;
                        do
                        {
                            if ((firstindex < 0) || (firstindex > index))
                                firstindex = index;

                            if (!bDisableLogging)
                                Log2("I: " + Convert.ToInt32(lNowMs - startTime) + " index=" + index + " firstindex=" + firstindex);

                            index--;
                            if (index < 0)
                                index = 255;
                        } while (dMH.ContainsKey(index));
                    }

                    index = firstindex;
                    if (!bDisableLogging)
                        Log2("I: " + Convert.ToInt32(lNowMs - startTime) + " firstindex=" + firstindex);

                    if (index <0) // Client disconnected 
                    {
                        string sMsg = "Client disconnected.";
                        Console.WriteLine("\n{0}", sMsg);
                        _serialPort.RtsEnable = false;
                        Log2(sMsg);
                        client.Close();
                        listener.Stop();
                        waveOut.Stop();
                        goto MyStart;
                    }
                    // now process all all msgHeader elements stored in the dictionary dMH
                    while (dMH.ContainsKey(index))
                    {
                        MsgHeader aH = dMH[index];

                        if (!bDisableLogging)
                            Log2("Server read: " + Convert.ToInt32(lNowMs - startTime) + " " +
                            bytesRead + " clientCounter=" + aH.clientCounter + " bitvector=" + Convert.ToString(aH.bitVector, 2).PadLeft(8, '0') +
                            " serverCounter=" + aH.serverCounter + " lMQ=" + lMQ.Count() + " lNQ=" + lNQ.Count());

                        // we sent to the client the serverCounter, which we now get back
                        // we can now calulate a roundtrip time seen on the server side
                        if (dServerTime.ContainsKey(aH.serverCounter)) {

                            long srvMs = dServerTime[aH.serverCounter];
                            int iPing = Convert.ToInt32(lNowMs - srvMs);
                            CheckPing(iPing);
                            lock(dServerTime)
                                dServerTime.Remove(aH.serverCounter);
                        }

                        // add a new server reference number and store the milliseconds since epoch in the dictionary dServerTime
                        aH.serverCounter = Convert.ToByte(iSrvCounter);
                        lock(dServerTime)
                            dServerTime[iSrvCounter] = lNowMs;

                        if (!bDisableLogging)
                            Log2("Server send: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " clientCounter=" +
                            aH.clientCounter + " bitvector=" + Convert.ToString(aH.bitVector, 2).PadLeft(8, '0') + " serverCounter=" + aH.serverCounter +
                            " lMQ=" + lMQ.Count() + " lNQ=" +lNQ.Count());


                        byte[] bytesToSend = InterOp.StructToByteArray(aH);
                        //---write back the text to the client---
                        nwStream.Write(bytesToSend, 0, bytesToSend.Length);

                        // we add the data to be send to the serial prot to the queue
                        SerialSend(aH);

                        index++;
                        if (index > 255)
                            index = 0;
                    } 

                    int iWaitMs = _waitMilliSecs - Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - lNowMs);
                    if (iWaitMs < 0)
                        iWaitMs = 0;
                    Thread.Sleep(iWaitMs);

                    if (iSrvCounter == 255)
                        iSrvCounter = 0;
                    else
                        iSrvCounter++;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Server exception: {0}", e);
                    _serialPort.RtsEnable = false;
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

    // Class for Morse stuff
    public static class Morse
    {
        // dictinary defining some morse alphabet
        public static Dictionary<string, char> morseDict = new Dictionary<string, char>() {
               {".-", 'a'}, {"-...", 'b'}, {"-.-.", 'c'}, {"-..", 'd'}, {".", 'e'}, {"..-.", 'f'},
               {"--.", 'g'}, {"....", 'h'}, {"..", 'i'}, {".---", 'j'}, {"-.-", 'k'}, {".-..", 'l'},
               {"--", 'm'}, {"-.", 'n'}, {"---", 'o'}, {".--.", 'p'}, {"--.-", 'q'}, {".-.", 'r'},
               {"...", 's'}, {"-", 't'}, {"..-", 'u'}, {"...-", 'v'}, {".--", 'w'}, {"-..-", 'x'},
               {"-.--", 'y'}, {"--..", 'z'}, {"-----", '0'}, {".----", '1'}, {"..---", '2'}, {"...--", '3'},
               {"....-", '4'}, {".....", '5'}, {"-....", '6'}, {"--...", '7'}, {"---..", '8'}, {"----.", '9'},
               {"-...-", '='}, {".-.-.", '+'}, {".-.-.-", '.'}, {"-..-.", '/'}, {"..--..", '?'}, {"--..--", ','},
               {"-....-", '-'}, {".-.-", 'ä'}, {"---.", 'ö'}, {"..--", 'ü'}, {"...--...", 'ß'}
        };


        private static long lMilliSecs;

        private static long oldONmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        private static long oldOFFmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        private static readonly MorseClustering amClust = new MorseClustering();

        private static double bestOn = 0;
        private static readonly List<char> mChar = new List<char>();

        private static bool bNewChar = true;
        private static bool bNewSpace = false;
        private static bool bCharOut = false;

        public static void ToOn()
        {
            lMilliSecs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            bNewChar = true;
            bCharOut = false;
            bNewSpace = false;

            oldONmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        }

        public static void ToOff()
        {

            lMilliSecs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            long deltaMs = lMilliSecs - oldONmilliseconds;
            amClust.AddArr(deltaMs);
            bestOn = amClust.GetBestThres();
            if (bestOn > 0)
            {
                // we can now determine if this is a dit or dah
                char c;
                if (deltaMs <= bestOn)
                    c = '.';
                else
                    c = '-';

                mChar.Add(c);
                oldOFFmilliseconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            }
        }

        public static void IsOff()
        {
            lMilliSecs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            long now = lMilliSecs - oldOFFmilliseconds;

            if (!bNewSpace && bCharOut && (now > 4.2 * bestOn))
            {
                Console.Write(" ");
                bNewSpace = true;
            }
            // simple approach to detect if we it the character boundary
            if (bNewChar && (now > 1.5 * bestOn))
            {
                if (mChar.Count > 0)
                {
                    string sMchar = new string(mChar.ToArray());
                    char fChar = '~';

                    // lookup for the corresponding letter in the morse dict
                    if (Morse.morseDict.ContainsKey(sMchar))
                    {
                        fChar = Morse.morseDict[sMchar];
                    }
                    Console.Write(fChar);

                    mChar.Clear();
                    oldONmilliseconds = now;
                }
                bNewChar = false;
                bCharOut = true;
            }
        }
    }

    // Simple emperical approach to distinguish dits from dahs, we use an array of the past 50 durations the key was on
    //  and split into 2 groups, calculate the variance of both groups
    //  the best fit is when the total variance is the minumum
    public class MorseClustering
    {
        public string myField = string.Empty;

        public MorseClustering()
        {
            //
        }

        public void AddArr(long value)
        {
            // Implement a buffer of the last onArrSize elements
            if (onArr.Count < onArrSize)
                onArr.Add(value);
            else {
                onArr.RemoveAt(0);
                onArr.Add(value);
            }
        }

        public double Mean(long[] lArr)
        {
            double lMean = 0;
            for (int i = 0; i < lArr.Length; i++)
                lMean += lArr[i];
            return lMean / lArr.Length;
        }

        public double Variance(long[] lArr)
        {
            double mean = Mean(lArr);
            double var = 0;
            for (int i = 0; i < lArr.Length; i++)
                var += Math.Pow(lArr[i] - mean, 2.0);
            return var / lArr.Length;
        }

        public double GetBestThres()
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
                        shorts.Add(onArr[j]);
                    else
                        longs.Add(onArr[j]);
                }

                if ((shorts.Count < 4) || (shorts.Count < 4))
                    continue;

                double vs = Variance(shorts.ToArray());
                double vl = Variance(longs.ToArray());
                double totalv = vs + vl;

                if (totalv < minvs) {
                    minvs = totalv;
                    bestThres = i;
                }
            }

            return bestThres;
        }

        private static readonly int onArrSize = 50;
        private readonly List<long> onArr = new List<long>();
        private double bestThres;
    }

    // the implementation for the key side
    public class Key : Jure
    {

        private static readonly ManualResetEvent receiveDone = new ManualResetEvent(false);
        private static readonly Dictionary<int, long> dCliTimeStamps = new Dictionary<int, long>();
        private static readonly List<int> lSrvNos = new List<int>();

        // init wave provider, we are using Sine Waves

        public static void Run() { 
            string message;
            StringComparer stringComparer = StringComparer.OrdinalIgnoreCase;
            Thread readThread = new Thread(Read); // we read from the rig with an own thread
            Thread sendThread = new Thread(NetSendThread); // we write to the rig with an own thread

            // Create a new SerialPort object with default settings.
            _serialPort = new SerialPort
            {

                // Set the port name
                PortName = "com" + iCom,

                // this is needed for reading pin6 and pin8
                DtrEnable = true
            };

            _serialPort.Open();
            _continue = true;
            readThread.Start();
            sendThread.Start();

            Console.WriteLine("Type QUIT to exit");

            while (_continue) {
                message = Console.ReadLine();

                if (stringComparer.Equals("quit", message))
                    _continue = false;
                else {
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
            StateObject state = new StateObject
            {
                workSocket = client
            };

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

            if (!client.Connected)
                return;

            // Read data from the remote device.  
            int bytesRead = client.EndReceive(ar);

            // Log2("Client ReceiveCallback bytesRead=" + bytesRead);

            if (bytesRead > 0) {
                // There might be more data, so store the data received so far.  
                // state.sb.Append(Encoding.ASCII.GetString(state.buffer, 0, bytesRead));
                state.bReceived.Add(state.buffer.ToArray());

                // Get the rest of the data.  
                client.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                    new AsyncCallback(ReceiveCallback), state);
            } else {
                // Signal that all bytes have been received.  
                receiveDone.Set();
            }

            if (state.bReceived.Count > 0) {
                for (int j = 0; j < state.bReceived.Count; j += 3) {
                    byte[] bufInstance = state.bReceived[0];
                    MsgHeader aHr = (MsgHeader)InterOp.ByteArrayToStruct(bufInstance, 0, typeof(MsgHeader));

                    if (!bDisableLogging) 
                        Log2("Client receive: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " + bytesRead + 
                        " clientCounter=" + aHr.clientCounter + " bitVector=" + Convert.ToString(aHr.bitVector, 2).PadLeft(8, '0') + 
                        " serverCounter=" + aHr.serverCounter + " lMQ=" + lMQ.Count() + " lNQ=" + lNQ.Count());

                    lSrvNos.Add(aHr.serverCounter);

                    if (dCliTimeStamps.ContainsKey(aHr.clientCounter)) {

                        long ms = dCliTimeStamps[aHr.clientCounter];
                        lock (dCliTimeStamps)
                            dCliTimeStamps.Remove(aHr.clientCounter);

                        int iPing = Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - ms);
                        avgPing = CheckPing(iPing);
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

            Console.WriteLine("Client connected with IP {0}", client.Client.RemoteEndPoint.ToString());

            while (_continue)
            {
                long ms1 = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                // we take over the data to send from the list lMQ into our privat list lNQ
                lock (lMQ) {
                    lock (lNQ)
                        lNQ.AddRange(lMQ);
                    lMQ.Clear();
                }

                if (lNQ.Count >100)
                {
                    Console.WriteLine("Client has a large queue, probably the communication to the server is broken. Giving up.");
                    _continue = false;
                }

                while (lNQ.Count > 0) {
                    MsgHeader aHs = lNQ[0];

                    byte[] bytesToSend = InterOp.StructToByteArray(aHs);
                    lNQ.RemoveAt(0);

                    if (!bDisableLogging) 
                        Log2("Client send: " + Convert.ToInt32(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - startTime) + " " + bytesToSend.Length + 
                        " clientCounter=" + aHs.clientCounter + " bitVector=" + Convert.ToString(aHs.bitVector, 2).PadLeft(8, '0') + 
                        " serverCounter=" + aHs.serverCounter + " lMQ=" + lMQ.Count() + " lNQ=" + lNQ.Count());
                    nwStream.Write(bytesToSend, 0, bytesToSend.Length);

                    Receive(client.Client);
                }

                long ms2 = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                int delta = Convert.ToInt32(ms2 - ms1);
                int s = _waitMilliSecs - delta;

                if (s > 0)
                    Thread.Sleep(s);
            }
            client.Close();
        }

        // we send a struct by adding it to the public list lMQ
        public static bool NetSend(MsgHeader msg)
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
                    if (iScanCount == 7) {

                        // the implemented protocol uses one byte to store a reference created by the client
                        // in our case it has the size of a byte (0..255), the counter is reset to 0 when it hits the boundary
                        // with the client refrence (0..255) the epoch time in ms is kept in a dictinoary,
                        // the server will send the client reference unmodified back to the client, and the client can calculate a rounttrip time 
                        
                        if (dCliTimeStamps.ContainsKey(iClientCount)) {
                            Log2("Warning: Client time stamp not cleared=" + iClientCount);

                            lock (dCliTimeStamps)
                                dCliTimeStamps.Remove(iClientCount); 
                        }
                        lock (dCliTimeStamps)
                            dCliTimeStamps.Add(iClientCount, lMilliSecs);

                        MsgHeader mHs;

                        mHs.bitVector = bOnOffStates;

                        // the client stores a list of reference numbers sent by the server
                        // when the client sends this back to the server, it removes it from the list as done
                        mHs.serverCounter = 0;

                        if (lSrvNos.Count > 0) {
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
                    if (pin6 && !pin6_last) {
                        //start the listening tone
                        waveOut.Play();

                        Morse.ToOn();
                    }

                    // now capture the case where the last status of the key was down (1) and the new is up (0).
                    if (!pin6 && pin6_last) {
                        // stop the listening tone
                        waveOut.Stop();

                        Morse.ToOff();
                    }

                    if (pin8 && !pin8_last) {
                        // needs to be implemented, implement keyer functionality 
                    }


                    if (!pin8 && pin8_last) {
                        // needs to be implemented, implement keyer functionality 
                    }

                    pin6_last = pin6;
                    pin8_last = pin8;

                    if (!pin6) 
                        Morse.IsOff();

                }
                catch (TimeoutException) { }

                Thread.Sleep(_waitMilliSecs);
            }
        }
    }

    // Wave implementation provided from https://markheath.net/post/playback-of-sine-wave-in-naudio
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

    // Sine Wave provider from https://markheath.net/post/playback-of-sine-wave-in-naudio
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
                if (sample >= sampleRate)
                    sample = 0;
            }
            return sampleCount;
        }
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

}
