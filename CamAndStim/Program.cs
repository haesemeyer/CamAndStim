using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Drawing;
using System.IO;
using NationalInstruments.DAQmx;

using AVT.VmbAPINET;

namespace CamAndStim
{
    class Program
    {
        static TiffWriter _imageWriter;

        static void Main(string[] args)
        {
            Dictionary<int, Tuple<int, int>> Cameras;
            Cameras = new Dictionary<int, Tuple<int, int>>();
            Cameras[1] = new Tuple<int, int>(2048, 2048);
            Cameras[0] = new Tuple<int, int>(492, 768);
            int cam_id = 1;
            int frameheight = Cameras[cam_id].Item1;
            int framewidth = Cameras[cam_id].Item2;
            Vimba camsession = new Vimba();
            CameraCollection cameras = null;
            camsession.Startup();
            cameras = camsession.Cameras;
            Console.WriteLine(cameras[0]);
            Console.WriteLine(cameras[1]);
            Console.WriteLine("Assuming that camera {0} is the main camera", cam_id);
            Console.WriteLine("Please enter the experiment name and press return:");
            string exp_name = Console.ReadLine();
            Camera av_cam = cameras[cam_id];
            Console.WriteLine("Starting laser tasks");
            StartLaserTasks();
            Console.WriteLine("Opening camera");
            av_cam.Open(VmbAccessModeType.VmbAccessModeFull);
            av_cam.LoadCameraSettings("CaSettings.xml");
            string today_folder = string.Format("{0}_{1}_{2}", DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
            _imageWriter = new TiffWriter("F://PatchCommander_Data//"+today_folder+"//"+exp_name, true);
            av_cam.OnFrameReceived += FrameReceived;
            av_cam.StartContinuousImageAcquisition(5000);//This is the maximum number of frames every aqcuired...
            Console.WriteLine("Started continuous capture. Press enter to exit");
            Console.ReadLine();
            camsession.Shutdown();
            _imageWriter.Dispose();
            StopLaserTasks();
        }

        /// <summary>
        /// The rate of analog out generation and ai readback
        /// for the laser
        /// </summary>
        static int rate = 100;

        /// <summary>
        /// The smoothed version of the current laser readback
        /// </summary>
        static double laser_aiv;

        /// <summary>
        /// Lock object for laser aiv
        /// </summary>
        static object laser_aiv_lock = new object();

        /// <summary>
        /// Event to signal stop to our write task
        /// </summary>
        static AutoResetEvent _writeStop = new AutoResetEvent(false);

        /// <summary>
        /// Event to signal stop to our read task
        /// </summary>
        static AutoResetEvent _readStop = new AutoResetEvent(false);

        static System.Threading.Tasks.Task _laserWriteTask;

        static System.Threading.Tasks.Task _laserReadTask;

        static void StartLaserTasks()
        {
            _laserWriteTask = new System.Threading.Tasks.Task(() =>
            {
                Task writeTask = new Task("LaserWrite");
                double[] firstSamples = sampleFunction(0, rate);
                writeTask.AOChannels.CreateVoltageChannel("Dev2/AO2", "", 0, 10, AOVoltageUnits.Volts);
                writeTask.Timing.ConfigureSampleClock("", rate, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples);
                writeTask.Stream.WriteRegenerationMode = WriteRegenerationMode.DoNotAllowRegeneration;
                while (!_writeStop.WaitOne(100))
                {

                }
                writeTask.Dispose();
            });

            _laserReadTask = new System.Threading.Tasks.Task(() =>
            {
                Task read_task = new Task("laserRead");
                read_task.AIChannels.CreateVoltageChannel("Dev2/ai16", "Laser", AITerminalConfiguration.Differential, -10, 10, AIVoltageUnits.Volts);
                read_task.Timing.ConfigureSampleClock("", rate, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples);
                read_task.Start();
                AnalogSingleChannelReader laser_reader = new AnalogSingleChannelReader(read_task.Stream);
                while (!_readStop.WaitOne(10))
                {
                    var nsamples = read_task.Stream.AvailableSamplesPerChannel;
                    if (nsamples >= 10)
                    {
                        double[] read = laser_reader.ReadMultiSample((int)nsamples);
                        lock (laser_aiv_lock)
                        {
                            foreach (double d in read)
                                //Simple exponential smoother
                                laser_aiv = 0.9 * laser_aiv + 0.1 * d;
                        }
                    }
                }
                read_task.Dispose();
            });
            _laserWriteTask.Start();
            _laserReadTask.Start();
        }

        static void StopLaserTasks()
        {
            _writeStop.Set();
            _readStop.Set();
            if (_laserWriteTask != null)
                _laserWriteTask.Wait();
            if (_laserReadTask != null)
                _laserReadTask.Wait();
        }

        /// <summary>
        /// Provides a rough encoding of laser ai voltage into the 0-255
        /// range corresponding to 0-4A current
        /// </summary>
        /// <param name="ai_voltage"></param>
        /// <returns></returns>
        static byte EncodeLaserStrength(double ai_voltage)
        {
            double fraction = -ai_voltage / 10;
            if (fraction < 0)
                fraction = 0;
            if (fraction > 1)
                fraction = 1;
            return (byte)(fraction * 255);
        }

        private static void FrameReceived(Frame frame)
        {
            byte[,] image = new byte[frame.Height, frame.Width];
            byte laser_pixel = 0;
            lock (laser_aiv_lock)
            {
                laser_pixel = EncodeLaserStrength(laser_aiv);
            }
            for (int i = 0; i < frame.BufferSize; i++)
            {
                long r, c;
                r = i / frame.Width;
                c = i % frame.Width;
                //Encode laser strength in the upper left corner
                if (r < 50 && c < 50)
                    image[r, c] = laser_pixel;
                else
                    image[r, c] = frame.Buffer[i];
            }
            _imageWriter.WriteFrame(image);
        }
    }
}
