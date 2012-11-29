using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using Microsoft.VisualBasic.Devices;
using System.Runtime.InteropServices;

namespace Motorola
{

    class Program
    {
        public delegate void WaveDelegate(IntPtr hdrvr, int uMsg, int dwUser, 
            ref WaveHdr wavhdr, int dwParam2);

        //static BinaryWriter bw;
        static public double accumulator = 0.0;
        static public double bitError = 0.0;
        static public int across = 0;

        static int sr = 0;
        static int bs = 0;
        static bool inSync = false;
        static bool[] ob = new bool[1023];
        static int obHead = 0, obTail = 0;
        static bool[] gob = new bool[100];
        static bool[] osw = new bool[50];
        static int good = 0;
        static int blocks = 0;
        static int ct = 0;


        public const int bufferSize = 8192;
        public const int baudRate = 3600;
        public const int sampleRate = 96000;
        public const double timePerBit = 1.0 / baudRate;
        public const double timePerHalfBit = timePerBit / 2.0;

        public const int MMSYSERR_NOERROR = 0; // no error

        public const int MM_WIM_OPEN = 0x3BE;
        public const int MM_WIM_CLOSE = 0x3BF;
        public const int MM_WIM_DATA = 0x3C0;

        public const int CALLBACK_FUNCTION = 0x00030000;

        private const string mmdll = "winmm.dll";
        
        [DllImport(mmdll)]
        public static extern int waveInAddBuffer(IntPtr hwi, ref WaveHdr pwh, int cbwh);
        [DllImport(mmdll)]
        public static extern int waveInClose(IntPtr hwi);
        [DllImport(mmdll)]
        public static extern int waveInOpen(out IntPtr phwi, int uDeviceID, 
            ref WaveFormat lpFormat, 
            WaveDelegate dwCallback, ref int dwInstance, int dwFlags);
        [DllImport(mmdll)]
        public static extern int waveInPrepareHeader(IntPtr hWaveIn, ref WaveHdr lpWaveInHdr, int uSize);
        [DllImport(mmdll)]
        public static extern int waveInUnprepareHeader(IntPtr hWaveIn, ref WaveHdr lpWaveInHdr, int uSize);
        [DllImport(mmdll)]
        public static extern int waveInStart(IntPtr hwi);
        [DllImport(mmdll)]
        public static extern int waveInStop(IntPtr hwi);

        static WaveDelegate d;

        static void Main(string[] args)
        {
            //StreamWriter sw = new StreamWriter("output.dat", false);
            //bw = new BinaryWriter(sw.BaseStream);

            int ret;
            IntPtr waveIn;
            WaveHdr waveHeader1 = new WaveHdr();
            WaveHdr waveHeader2 = new WaveHdr();
            GCHandle wh1 = GCHandle.Alloc(waveHeader1, GCHandleType.Pinned);
            GCHandle wh2 = GCHandle.Alloc(waveHeader2, GCHandleType.Pinned);

            
            WaveFormat wf = new WaveFormat();
            
            wf.wFormatTag = 1; // 1 is PCM
            wf.nChannels = 1;
            wf.nSamplesPerSec = sampleRate;
            wf.wBitsPerSample = 16;
            wf.nBlockAlign = 2;
            wf.cbSize = 0;
            wf.nAvgBytesPerSec = wf.nSamplesPerSec * wf.wBitsPerSample / 8;

            d = new WaveDelegate(waveInCallback);
            int devID = -1;

            
            int callbackInstance = 0;

            ret = waveInOpen(out waveIn, devID, ref wf, d, 
                ref callbackInstance, CALLBACK_FUNCTION);
            if (ret != 0)
                return;
            waveHeader1.dwBufferLength = bufferSize;
            waveHeader2.dwBufferLength = bufferSize;
            waveHeader1.lpData = Marshal.AllocHGlobal(bufferSize);
            waveHeader2.lpData = Marshal.AllocHGlobal(bufferSize);

            ret = waveInPrepareHeader(waveIn, ref waveHeader1, Marshal.SizeOf(waveHeader1));
            ret = waveInAddBuffer(waveIn, ref waveHeader1, Marshal.SizeOf(waveHeader1));
            ret = waveInPrepareHeader(waveIn, ref waveHeader2, Marshal.SizeOf(waveHeader2));
            ret = waveInAddBuffer(waveIn, ref waveHeader2, Marshal.SizeOf(waveHeader2));

            ret = waveInStart(waveIn);
            Keyboard keyboard = new Keyboard();
            while (true)
            {
                Thread.Sleep(100);
                if (keyboard.AltKeyDown == true)
                    break;
            }

            ret = waveInStop(waveIn);
            ret = waveInClose(waveIn);
            //bw.Close();
        }

        static void waveInCallback(IntPtr hdrvr, int uMsg, int dwUser,
            ref WaveHdr wavehdr, int dwParam2)
        {
//            byte[] soundBuffer = new byte[bufferSize];

            if (uMsg == MM_WIM_DATA)
            {
//                Marshal.Copy(wavehdr.lpData, soundBuffer, 0, bufferSize);

//                bw.Write(soundBuffer);
                waveInUnprepareHeader(hdrvr, ref wavehdr, Marshal.SizeOf(wavehdr));
                waveInPrepareHeader(hdrvr, ref wavehdr, Marshal.SizeOf(wavehdr));

                waveInAddBuffer(hdrvr, ref wavehdr, Marshal.SizeOf(wavehdr));
                analyze(wavehdr);
            }
        }

        static void analyze(WaveHdr wavehdr)
        {
            int index = 0, count = 0;
            int size = wavehdr.dwBytesRecorded / sizeof(short);
            short[] soundBuffer = new short[size];

            Marshal.Copy(wavehdr.lpData, soundBuffer, 0, size);


            while (index < size)
            {
                bool bit = soundBuffer[index++] > 0;

                count++;

                while (index < size && bit == (bool)(soundBuffer[index] > 0))
                {
                    index++;
                    count++;
                }

                processBit(bit, count);
                count = 0;

            }
        }

        static void processBit(bool bit, int count)
        {
            /*
            for (int i = 0; i < count / 10; i++)
            {
                handleBit(bit);
            } */
            
            double delta = count;

            delta /= sampleRate;
            accumulator += delta;

            double fastCompare = bitError + timePerHalfBit;

            while (accumulator >= fastCompare)
            {
                handleBit(bit);
                accumulator -= timePerBit;
            }

            if (bit)
                bitError += (accumulator - bitError) / 15.0;
        }

        static void handleBit(bool bit)
        {

            const int sync = 0xac;

            sr = (sr << 1) & 0xff;

            if (bit)
                sr |= 0x01;

            ob[bs] = bit;

            if (sr == sync)
            {
                //Console.WriteLine("bs=" + bs);

                if (bs > 83)
                {
                    deinterleave(bs-83);
                    inSync = true;
                }
            }
            if (bs < 989)
            {
                bs++;
            }
            else
            {
                bs = 0;
                inSync = false;
                
            }
        }

        static void deinterleave(int skip)
        {
            int i1, i2;

            ct = 0;

            for(i1 = 0; i1 < 19; ++i1)
            {
                for(i2 = 0; i2 < 4; ++i2)
                {
                    processOSW(ob[((i2*19) + i1) + skip]);
                }
            }
        }

        static void processOSW(bool bit)
        {
            int i;
            int sr, sax, f1, f2, iid, cmd, neb;
            OSW computedOSW = new OSW();

            gob[ct++] = bit;

            if (ct == 76)
            {
                if (blocks == 43)
                {
                    //Console.WriteLine("{0}%", ((double)good / (double)blocks) * 100.0);
                    blocks = 0; good = 0;
                }
                blocks++;
                sr = 0x036e;
                sax = 0x0393;
                neb = 0;

                for (i = 0; i < 76; i += 2)
                {
                    osw[i >> 1] = gob[i];

                    if (gob[i])
                    {
                        gob[i]     ^= true;
                        gob[i + 1] ^= true;
                        gob[i + 3] ^= true;
                    }
                }

                for (i = 0; i < 76; i += 2)
                {
                    if (gob[i + 1] && gob[i + 3])
                    {
                        osw[i >> 1] ^= true;
                        gob[i + 1] ^= true;
                        gob[i + 3] ^= true;
                    }
                }
                for (i = 0; i < 27; i++)
                {
                    if ((sr & 1) == 1)
                        sr = (sr >> 1) ^ 0x0225;
                    else
                        sr >>= 1;

                    if (osw[i])
                        sax = sax ^ sr;
                }

                for (i = 0; i < 10; i++)
                {
                    f1 = osw[36 - i] ? 0 : 1;
                    f2 = sax & 1;

                    sax >>= 1;

                    if (f1 != f2)
                        neb++;
                }
                if (neb == 0)
                {
                    good++;
                    bs = 0;
                    for (iid = 0, i = 0; i < 16; i++)
                    {
                        iid = iid << 1;

                        if (!osw[i])
                            iid++;
                    }
                    computedOSW.ID = (short)(iid ^ 0x33c7);
                    computedOSW.group = (osw[16] ^ true);

                    for (cmd = 0, i = 17; i < 27; i++)
                    {
                        cmd <<= 1;

                        if (!osw[i])
                            cmd++;
                    }

                    computedOSW.command = (short)(cmd ^ 0x032a);

                    showGoodOSW(computedOSW);
                }
                else
                {
                    Console.Write("Bad         ");
                    newline();


                    //showBadOSW(computedOSW);
                }


            }   
        }

        static void showGoodOSW(OSW computedOSW)
        {
            Console.Write("{0:X4} {1} {2:X4} ", computedOSW.ID, 
                computedOSW.group ? "G" : "I",
                computedOSW.command);
            newline();
        
          // Console.WriteLine("Good");
           
        }

        static void newline()
        {
            across++;

            if (across > 0)
            {
                Console.WriteLine();
                across = 0;
            }

        }

    }

    public struct OSW
    {
        public short command;
        public short ID;
        public bool group;
        public bool isFrequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WaveFormat
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)] 
    public struct WaveHdr
	{
		public IntPtr lpData; // pointer to locked data buffer
		public int dwBufferLength; // length of data buffer
		public int dwBytesRecorded; // used for input only
		public IntPtr dwUser; // for client's use
		public int dwFlags; // assorted flags (see defines)
		public int dwLoops; // loop control counter
		public IntPtr lpNext; // PWaveHdr, reserved for driver
		public IntPtr reserved; // reserved for driver
	}


    
}
