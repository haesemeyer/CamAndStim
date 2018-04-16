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
        #region Members
        static TiffWriter _imageWriter;

        /// <summary>
        /// The number of seconds before and after each laser pulse
        /// </summary>
        static uint _laserPrePostSeconds;

        /// <summary>
        /// The length of each laser pulse in seconds
        /// </summary>
        static uint _laserOnSeconds;

        /// <summary>
        /// The desired laser current in mA during the pulse
        /// </summary>
        static double _laserCurrentmA;

        /// <summary>
        /// The number of laser stimulus presentations
        /// </summary>
        static uint _n_stim;

        /// <summary>
        /// The rate of analog out generation and ai readback
        /// for the laser
        /// </summary>
        static int _rate = 100;

        /// <summary>
        /// The smoothed version of the current laser readback
        /// </summary>
        static double _laser_aiv;

        /// <summary>
        /// Lock object for laser aiv
        /// </summary>
        static object _laser_aiv_lock = new object();

        /// <summary>
        /// Event to signal stop to our write task
        /// </summary>
        static AutoResetEvent _writeStop = new AutoResetEvent(false);

        /// <summary>
        /// Event to signal stop to our read task
        /// </summary>
        static AutoResetEvent _readStop = new AutoResetEvent(false);

        /// <summary>
        /// The task that writes analog out samples to control the laser
        /// </summary>
        static System.Threading.Tasks.Task _laserWriteTask;

        /// <summary>
        /// The task that reads analog in samples to get the laser strength
        /// </summary>
        static System.Threading.Tasks.Task _laserReadTask;

        /// <summary>
        /// The default camera id
        /// </summary>
        static int _cam_id_default;

        #endregion

        static void Main(string[] args)
        {
            LoadUserSettings();
            //Hook Ctrl+C and Ctrl+Break to be able to gracefully shut down laser
            Console.CancelKeyPress += Console_CancelKeyPress;
            //Obtain information on connected cameras
            Vimba camsession = new Vimba();
            CameraCollection cameras = null;
            camsession.Startup();
            cameras = camsession.Cameras;
            //Write camera information to console
            for (int i = 0; i < cameras.Count; i++)
                Console.WriteLine("Camera {0} is : {1}", i, cameras[i]);
            //Write default camera and paradigm structure to console
            Console.WriteLine("############################################################");
            Console.WriteLine("Paradigm defaults:");
            Console.WriteLine("Assuming that camera {0} is the main camera", _cam_id_default);
            Console.WriteLine("Pre/post stimulus = {0} seconds.", _laserPrePostSeconds);
            Console.WriteLine("Stimulus ON = {0} seconds.", _laserOnSeconds);
            Console.WriteLine("Laser stimulus current = {0} mA.", _laserCurrentmA);
            Console.WriteLine("Number of stimulus trials = {0}.", _n_stim);
            Console.WriteLine("############################################################");
            string answer = "";
            while(answer != "a" && answer != "e")
            {
                Console.Write("accept defaults or edit? [a/e]");
                answer = Console.ReadLine();
            }
            if (answer == "e")
                AskParadigmInput();
            Console.WriteLine("Please enter the experiment name and press return:");
            string exp_name = Console.ReadLine();
            Camera av_cam = cameras[_cam_id_default];
            Console.WriteLine("Starting laser tasks");
            StartLaserTasks();
            Console.WriteLine("Opening camera");
            av_cam.Open(VmbAccessModeType.VmbAccessModeFull);
            try
            {
                av_cam.LoadCameraSettings("CaSettings.xml");
            }
            catch
            {
                Console.WriteLine("Could not find camera configuration file CaSettings.xml");
                Console.WriteLine("Reverting to whatever defaults are currently set...");
            }
            string today_folder = string.Format("{0}_{1}_{2}", DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
            _imageWriter = new TiffWriter("F://PatchCommander_Data//"+today_folder+"//"+exp_name, true);
            av_cam.OnFrameReceived += FrameReceived;
            av_cam.StartContinuousImageAcquisition(5000);//This is the maximum number of frames ever aqcuired...
            double total_seconds = _n_stim * (2 * _laserPrePostSeconds + _laserOnSeconds);
            Console.WriteLine("Started continuous capture. Total length: {0} seconds", total_seconds);
            while(total_seconds > 0)
            {
                Thread.Sleep(2000);
                total_seconds -= 2;
                Console.WriteLine("{0} seconds remaining.", total_seconds);
            }
            camsession.Shutdown();
            _imageWriter.Dispose();
            StopLaserTasks();
            SaveUserSettings();
            Console.WriteLine("Finished successfully.");
            Thread.Sleep(1000);
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            StopLaserTasks();
        }

        #region Methods
        
        /// <summary>
        /// Asks user to enter paradigm information
        /// </summary>
        static void AskParadigmInput()
        {
            Console.Write("Please enter the camera index to use:");
            while (!int.TryParse(Console.ReadLine(), out _cam_id_default))
                Console.WriteLine("Invalid input. Has to be integer.");
            Console.Write("Please enter the number of seconds pre/post stimulus:");
            while(!uint.TryParse(Console.ReadLine(), out _laserPrePostSeconds))
                Console.WriteLine("Invalid input. Has to be positive integer.");
            Console.Write("Please enter the number of stimulus seconds:");
            while (!uint.TryParse(Console.ReadLine(), out _laserOnSeconds))
                Console.WriteLine("Invalid input. Has to be positive integer.");
            Console.Write("Please enter the laser current in mA:");
            while (!double.TryParse(Console.ReadLine(), out _laserCurrentmA))
                Console.WriteLine("Invalid input. Has to be numeric.");
            Console.Write("Please enter the number of stimulus trials:");
            while (!uint.TryParse(Console.ReadLine(), out _n_stim))
                Console.WriteLine("Invalid input. Has to be positive integer.");
        }

        /// <summary>
        /// Load user settings
        /// </summary>
        static void LoadUserSettings()
        {
            _cam_id_default = Properties.Settings.Default.DefaultCam;
            _laserCurrentmA = Properties.Settings.Default.LCurrent;
            _laserOnSeconds = Properties.Settings.Default.StimS;
            _laserPrePostSeconds = Properties.Settings.Default.PrePostS;
            _n_stim = Properties.Settings.Default.NStim;
        }

        static void SaveUserSettings()
        {
            Properties.Settings.Default.DefaultCam = _cam_id_default;
            Properties.Settings.Default.LCurrent = _laserCurrentmA;
            Properties.Settings.Default.StimS = _laserOnSeconds;
            Properties.Settings.Default.PrePostS = _laserPrePostSeconds;
            Properties.Settings.Default.NStim = _n_stim;
            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Starts laser read and write tasks
        /// </summary>
        static void StartLaserTasks()
        {
            _laserWriteTask = new System.Threading.Tasks.Task(() =>
            {
                Task writeTask = new Task("LaserWrite");
                double[] firstSamples = LaserFunction(0, _rate);
                writeTask.AOChannels.CreateVoltageChannel("Dev2/AO2", "", 0, 10, AOVoltageUnits.Volts);
                writeTask.Timing.ConfigureSampleClock("", _rate, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples);
                writeTask.Stream.WriteRegenerationMode = WriteRegenerationMode.DoNotAllowRegeneration;
                AnalogSingleChannelWriter dataWriter = new AnalogSingleChannelWriter(writeTask.Stream);
                dataWriter.WriteMultiSample(false, firstSamples);
                writeTask.Start();
                long start_sample = _rate;
                while (!_writeStop.WaitOne(100))
                {
                    double[] samples = LaserFunction(start_sample, _rate);
                    if (samples == null)
                        break;
                    dataWriter.WriteMultiSample(false, samples);
                    start_sample += _rate;
                }
                writeTask.Dispose();
                Task resetTask = new Task("LaserReset");
                resetTask.AOChannels.CreateVoltageChannel("Dev2/AO2", "", 0, 10, AOVoltageUnits.Volts);
                AnalogSingleChannelWriter resetWriter = new AnalogSingleChannelWriter(resetTask.Stream);
                resetWriter.WriteSingleSample(true, 0);
                resetTask.Dispose();
            });

            _laserReadTask = new System.Threading.Tasks.Task(() =>
            {
                Task read_task = new Task("laserRead");
                read_task.AIChannels.CreateVoltageChannel("Dev2/ai16", "Laser", AITerminalConfiguration.Differential, -10, 10, AIVoltageUnits.Volts);
                read_task.Timing.ConfigureSampleClock("", _rate, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples);
                read_task.Start();
                AnalogSingleChannelReader laser_reader = new AnalogSingleChannelReader(read_task.Stream);
                while (!_readStop.WaitOne(10))
                {
                    var nsamples = read_task.Stream.AvailableSamplesPerChannel;
                    if (nsamples >= 10)
                    {
                        double[] read = laser_reader.ReadMultiSample((int)nsamples);
                        lock (_laser_aiv_lock)
                        {
                            foreach (double d in read)
                                //Simple exponential smoother
                                _laser_aiv = 0.9 * _laser_aiv + 0.1 * d;
                        }
                    }
                }
                read_task.Dispose();
            });
            _laserWriteTask.Start();
            _laserReadTask.Start();
        }

        /// <summary>
        /// Sample generation function for laser stimulus
        /// </summary>
        /// <param name="startSample">The index of the first sample to generate</param>
        /// <param name="n_samples">The number of samples to generate</param>
        /// <returns>The corresponding analog voltage samples</returns>
        private static double[] LaserFunction(long startSample, int n_samples)
        {
            long peri_samples = _laserPrePostSeconds * _rate;
            long stim_samples = _laserOnSeconds * _rate;
            long cycle_length = 2 * peri_samples + stim_samples;
            double[] samples = new double[n_samples];
            for(int i = 0; i < n_samples; i++)
            {
                long currsample = startSample + i;
                var cycle = currsample / cycle_length;
                if (cycle >= _n_stim) //we are beyond the end of our protocol
                    break;
                if (currsample % cycle_length > peri_samples && currsample % cycle_length < stim_samples + peri_samples)
                    samples[i] = LaserCurrentToAoV(_laserCurrentmA);
            }
            return samples;
        }

        /// <summary>
        /// Convenience function to convert a desired laser diode current
        /// to an analog out voltage
        /// </summary>
        /// <param name="laserCurrentmA">The desired current in mA</param>
        /// <returns>The analog out control voltage</returns>
        private static double LaserCurrentToAoV(double laserCurrentmA)
        {
            var ret = laserCurrentmA / 4000 * 10;
            if (ret < 0)
                ret = 0;
            if (ret > 10)
                ret = 10;
            return ret;
        }

        /// <summary>
        /// Stops the laser tasks and waites for them to finish
        /// </summary>
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

        /// <summary>
        /// Event handler for frames received from the camera
        /// </summary>
        /// <param name="frame">The received frame</param>
        private static void FrameReceived(Frame frame)
        {
            byte[,] image = new byte[frame.Height, frame.Width];
            byte laser_pixel = 0;
            lock (_laser_aiv_lock)
            {
                laser_pixel = EncodeLaserStrength(_laser_aiv);
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
        #endregion
    }
}
