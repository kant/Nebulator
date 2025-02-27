﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using NLog;
using Newtonsoft.Json;
using NAudio.Midi;
using NAudio.Wave;
using NBagOfTricks.Utils;
using NBagOfTricks.UI;
using Nebulator.Common;
using Nebulator.Script;
using Nebulator.Device;
using Nebulator.Server;
using Nebulator.Midi;
using Nebulator.OSC;



namespace Nebulator
{
    public partial class MainForm : Form
    {
        #region Enums
        /// <summary>Internal status.</summary>
        enum PlayCommand { Start, Stop, Rewind, StopRewind, UpdateUiTime }
        #endregion

        #region Fields
        /// <summary>App logger.</summary>
        Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>Fast timer.</summary>
        MmTimerEx _timer = new MmTimerEx();

        /// <summary>The current script.</summary>
        NebScript _script = null;

        /// <summary>Seconds since start pressed.</summary>
        DateTime _startTime = DateTime.Now;

        /// <summary>Current step time clock.</summary>
        Time _stepTime = new Time();

        /// <summary>Script compile errors and warnings.</summary>
        List<ScriptError> _compileResults = new List<ScriptError>();

        /// <summary>Current np file name.</summary>
        string _fn = Utils.UNKNOWN_STRING;

        /// <summary>Detect changed script files.</summary>
        MultiFileWatcher _watcher = new MultiFileWatcher();

        /// <summary>Files that have been changed externally or have runtime errors - requires a recompile.</summary>
        bool _needCompile = false;

        /// <summary>The temp dir for compile products.</summary>
        string _compileTempDir = "";

        /// <summary>Persisted internal values for current script file.</summary>
        Bag _nppVals = new Bag();

        /// <summary>Diagnostics for timing measurement.</summary>
        TimingAnalyzer _tan = new TimingAnalyzer() { SampleSize = 100 };

        /// <summary>Server host.</summary>
        SelfHost _selfHost = null;

        /// <summary>Devices to use for send.</summary>
        Dictionary<string, NOutput> _outputs = new Dictionary<string, NOutput>();

        /// <summary>Devices to use for recv.</summary>
        Dictionary<string, NInput> _inputs = new Dictionary<string, NInput>();
        #endregion

        #region Lifecycle
        /// <summary>
        /// Constructor.
        /// </summary>
        public MainForm()
        {
            // Need to load settings before creating controls in MainForm_Load().
            string appDir = MiscUtils.GetAppDataDir("Nebulator");
            DirectoryInfo di = new DirectoryInfo(appDir);
            di.Create();
            UserSettings.Load(appDir);
            InitializeComponent();
            toolStrip1.Renderer = new NBagOfTricks.UI.CheckBoxRenderer() { SelectedColor = UserSettings.TheSettings.SelectedColor };
        }

        /// <summary>
        /// Initialize form controls.
        /// </summary>
        void MainForm_Load(object sender, EventArgs e)
        {
            #region Init UI from settings
            // Main form.
            Location = new Point(UserSettings.TheSettings.MainFormInfo.X, UserSettings.TheSettings.MainFormInfo.Y);
            Size = new Size(UserSettings.TheSettings.MainFormInfo.Width, UserSettings.TheSettings.MainFormInfo.Height);
            WindowState = FormWindowState.Normal;
            BackColor = UserSettings.TheSettings.BackColor;

            // The rest of the controls.
            textViewer.WordWrap = false;
            textViewer.BackColor = UserSettings.TheSettings.BackColor;
            textViewer.Font = UserSettings.TheSettings.EditorFont;
            textViewer.Colors.Add("ERROR:", Color.LightPink);
            textViewer.Colors.Add("WARNING:", Color.Plum);

            btnMonIn.Image = MiscUtils.ColorizeBitmap(btnMonIn.Image, UserSettings.TheSettings.IconColor);
            btnMonOut.Image = MiscUtils.ColorizeBitmap(btnMonOut.Image, UserSettings.TheSettings.IconColor);
            btnKillComm.Image = MiscUtils.ColorizeBitmap(btnKillComm.Image, UserSettings.TheSettings.IconColor);
            btnSettings.Image = MiscUtils.ColorizeBitmap(btnSettings.Image, UserSettings.TheSettings.IconColor);
            btnAbout.Image = MiscUtils.ColorizeBitmap(btnAbout.Image, UserSettings.TheSettings.IconColor);
            fileDropDownButton.Image = MiscUtils.ColorizeBitmap(fileDropDownButton.Image, UserSettings.TheSettings.IconColor);
            btnRewind.Image = MiscUtils.ColorizeBitmap(btnRewind.Image, UserSettings.TheSettings.IconColor);
            btnCompile.Image = MiscUtils.ColorizeBitmap(btnCompile.Image, UserSettings.TheSettings.IconColor);
            btnClear.Image = MiscUtils.ColorizeBitmap(btnClear.Image, UserSettings.TheSettings.IconColor);
            btnWrap.Image = MiscUtils.ColorizeBitmap(btnWrap.Image, UserSettings.TheSettings.IconColor);

            btnMonIn.Checked = UserSettings.TheSettings.MonitorInput;
            btnMonOut.Checked = UserSettings.TheSettings.MonitorOutput;

            chkPlay.Image = MiscUtils.ColorizeBitmap(chkPlay.Image, UserSettings.TheSettings.IconColor);
            chkPlay.BackColor = UserSettings.TheSettings.BackColor;
            chkPlay.FlatAppearance.CheckedBackColor = UserSettings.TheSettings.SelectedColor;

            potSpeed.DrawColor = UserSettings.TheSettings.IconColor;
            potSpeed.BackColor = UserSettings.TheSettings.BackColor;
            potSpeed.Font = UserSettings.TheSettings.ControlFont;
            potSpeed.Invalidate();

            sldVolume.DrawColor = UserSettings.TheSettings.ControlColor;
            sldVolume.Font = UserSettings.TheSettings.ControlFont;
            sldVolume.DecPlaces = 2;
            sldVolume.Invalidate();

            timeMaster.ControlColor = UserSettings.TheSettings.ControlColor;
            timeMaster.Invalidate();

            if (UserSettings.TheSettings.CpuMeter)
            {
                CpuMeter cpuMeter = new CpuMeter()
                {
                    Width = 50,
                    Height = toolStrip1.Height,
                    DrawColor = Color.Red
                };
                // This took way too long to find out:
                //https://stackoverflow.com/questions/12823400/statusstrip-hosting-a-usercontrol-fails-to-call-usercontrols-onpaint-event
                cpuMeter.MinimumSize = cpuMeter.Size;
                cpuMeter.Enable = true;
                toolStrip1.Items.Add(new ToolStripControlHost(cpuMeter));
            }

            btnClear.Click += (object _, EventArgs __) => { textViewer.Clear(); };
            btnWrap.Click += (object _, EventArgs __) => { textViewer.WordWrap = btnWrap.Checked; };
            #endregion

            importMidiToolStripMenuItem.Visible = false; // TODO fix this sometime.

            InitLogging();

            PopulateRecentMenu();

            ScriptDefinitions.TheDefinitions.Init();

            // Fast mm timer.
            _timer = new MmTimerEx();
            SetSpeedTimerPeriod();
            _timer.TimerElapsedEvent += TimerElapsedEvent;
            _timer.Start();

            timerHousekeep.Interval = 500;
            timerHousekeep.Start();

            KeyPreview = true; // for routing kbd strokes properly

            _watcher.FileChangeEvent += Watcher_Changed;

            // Init HTTP server.
            _selfHost = new SelfHost();
            SelfHost.RequestEvent += SelfHost_RequestEvent;
            Task.Run(() => { _selfHost.Run(); });

            #region System info
            Text = $"Nebulator {MiscUtils.GetVersionString()} - No file loaded";
            #endregion

            #region Open file
            string sopen = "";
            
            // Look for filename passed in.
            string[] args = Environment.GetCommandLineArgs();
            if (args.Count() > 1)
            {
                sopen = OpenFile(args[1]);
            }

            if (sopen == "")
            {
                ProcessPlay(PlayCommand.Stop, true);
            }
            else
            {
                _logger.Error(sopen);

            }
            #endregion
        }

        /// <summary>
        /// Clean up on shutdown.
        /// </summary>
        void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            ProcessPlay(PlayCommand.Stop, false);

            // Just in case.
            _outputs.ForEach(o => o.Value?.Kill());

            DeleteDevices();

            if (_script != null)
            {
                // Save the project.
                SaveProjectValues();
                _script.Dispose();
            }

            // Save user settings.
            SaveSettings();
        }

        /// <summary>
        /// Resource clean up.
        /// </summary>
        /// <param name="disposing"></param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;

                _selfHost?.Dispose();

                DeleteDevices();

                components?.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Diagnostics.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void timerHousekeep_Tick(object sender, EventArgs e)
        {
        }
        #endregion

        #region Device management
        /// <summary>
        /// Dispose of all current devices.
        /// </summary>
        void DeleteDevices()
        {
            // Save the vkbd position.
            _inputs.Values.Where(v => v.GetType() == typeof(Keyboard)).ForEach
               (k => UserSettings.TheSettings.VirtualKeyboardInfo.FromForm(k as Keyboard));

            _inputs.ForEach(i => { i.Value?.Stop(); i.Value?.Dispose(); });
            _inputs.Clear();
            _outputs.ForEach(o => { o.Value?.Stop(); o.Value?.Dispose(); });
            _outputs.Clear();
        }

        /// <summary>
        /// Create all devices from user script.
        /// </summary>
        void CreateDevices()
        {
            // Clean up for company.
            DeleteDevices();

            // Get requested inputs.
            Keyboard vkey = null; // If used, requires special handling.

            foreach (NController ctlr in _script.Controllers)
            {
                // Have we seen it yet?
                if (_inputs.ContainsKey(ctlr.DeviceName))
                {
                    ctlr.Device = _inputs[ctlr.DeviceName];
                }
                else // nope
                {
                    NInput nin = null;

                    List<string> parts = ctlr.DeviceName.SplitByToken(":");
                    if (parts.Count >= 1)
                    {
                        switch (parts[0].ToUpper())
                        {
                            case "MIDI":
                                nin = new MidiInput();
                                break;

                            case "OSC":
                                nin = new OscInput();
                                break;

                            case "VKEY":
                                vkey = new Keyboard();
                                nin = vkey;
                                break;
                        }
                    }

                    // Finish it up.
                    if (nin != null)
                    {
                        if (nin.Init(ctlr.DeviceName))
                        {
                            nin.DeviceInputEvent += Device_InputEvent;
                            nin.DeviceLogEvent += Device_LogEvent;
                            ctlr.Device = nin;
                            _inputs.Add(ctlr.DeviceName, nin);
                        }
                        else
                        {
                            _logger.Error($"Failed to init controller: {ctlr.DeviceName}");
                        }
                    }
                    else
                    {
                        _logger.Error($"Invalid controller: {ctlr.DeviceName}");
                    }
                }

                if(vkey != null)
                {
                    vkey.StartPosition = FormStartPosition.Manual;
                    vkey.Size = new Size(UserSettings.TheSettings.VirtualKeyboardInfo.Width, UserSettings.TheSettings.VirtualKeyboardInfo.Height);
                    vkey.TopMost = false;
                    vkey.Location = new Point(UserSettings.TheSettings.VirtualKeyboardInfo.X, UserSettings.TheSettings.VirtualKeyboardInfo.Y);
                    vkey.Show();
                }
            }

            // Get requested outputs.
            foreach (NChannel chan in _script.Channels)
            {
                // Have we seen it yet?
                if (_outputs.ContainsKey(chan.DeviceName))
                {
                    chan.Device = _outputs[chan.DeviceName];
                }
                else // nope
                {
                    NOutput nout = null;

                    List<string> parts = chan.DeviceName.SplitByToken(":");
                    if (parts.Count >= 1)
                    {
                        switch (parts[0].ToUpper())
                        {
                            case "MIDI":
                                nout = new MidiOutput();
                                break;

                            case "OSC":
                                nout = new OscOutput();
                                break;
                        }
                    }

                    // Finish it up.
                    if (nout != null)
                    {
                        nout.DeviceLogEvent += Device_LogEvent;

                        if (nout.Init(chan.DeviceName))
                        {
                            chan.Device = nout;
                            _outputs.Add(chan.DeviceName, nout);
                        }
                        else
                        {
                            _logger.Error($"Failed to init channel: {chan.DeviceName}");
                        }
                    }
                    else
                    {
                        _logger.Error($"Invalid channel: {chan.DeviceName}");
                    }
                }
            }
        }
        #endregion

        #region Server processing
        /// <summary>
        /// Process a request from the web api. Set the e.Result to a json string. 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void SelfHost_RequestEvent(object sender, SelfHost.RequestEventArgs e)
        {
            Console.WriteLine($"MainForm Web request:{e.Request}");

            // What is wanted?
            List<string> parts = e.Request.SplitByToken("/");
            string[] validCmds = { "open", "compile", "start", "stop", "rewind" };

            switch (parts[0])
            {
                case "open":
                    string fn = string.Join("/", parts.GetRange(1, parts.Count - 1));
                    string res = OpenFile(fn);
                    e.Result = JsonConvert.SerializeObject(res == "" ? SelfHost.OK_NO_DATA : res, Formatting.Indented);
                    break;

                case "compile":
                    bool compok = Compile();
                    e.Result = JsonConvert.SerializeObject(_compileResults, Formatting.Indented);
                    break;

                case "start":
                    bool playok = false;
                    playok = ProcessPlay(PlayCommand.Start, false);
                    e.Result = JsonConvert.SerializeObject(playok ? SelfHost.OK_NO_DATA : SelfHost.FAIL, Formatting.Indented);
                    break;

                case "stop":
                    ProcessPlay(PlayCommand.Stop, false);
                    e.Result = JsonConvert.SerializeObject(SelfHost.OK_NO_DATA, Formatting.Indented);
                    break;

                case "rewind":
                    ProcessPlay(PlayCommand.Rewind, false);
                    e.Result = JsonConvert.SerializeObject(SelfHost.OK_NO_DATA, Formatting.Indented);
                    break;

                default:
                    // Invalid.
                    e.Result = JsonConvert.SerializeObject(null, Formatting.Indented);
                    break;
            }

            Console.WriteLine($"MainForm Result:{e.Result}");
        }
        #endregion

        #region Compile
        /// <summary>
        /// Master compiler function.
        /// </summary>
        bool Compile()
        {
            bool ok = true;

            if (_fn == Utils.UNKNOWN_STRING)
            {
                _logger.Warn("No script file loaded.");
                ok = false;
            }
            else
            {
                NebCompiler compiler = new NebCompiler() { Min = false };

                // Clean up any old.
                _script?.Dispose();

                // Compile script now.
                _script = compiler.Execute(_fn);

                // Update file watcher - keeps an eye on any included files too.
                _watcher.Clear();
                compiler.SourceFiles.ForEach(f => { if (f != "") _watcher.Add(f); });

                // Time points.
                timeMaster.TimeDefs.Clear();

                // Process errors. Some may be warnings.
                _compileResults = compiler.Errors;
                int errorCount = _compileResults.Count(w => w.ErrorType == ScriptErrorType.Error);

                if (errorCount == 0 && _script != null)
                {
                    SetCompileStatus(true);
                    _compileTempDir = compiler.TempDir;

                    // Note: Need exception handling here to protect from user script errors.
                    try
                    {
                        // Surface area.
                        InitRuntime();

                        // Setup - first step.
                        _script.Setup();

                        // Devices are specified in script.Setup() - create now.
                        CreateDevices();

                        // Setup - optional second step.
                        _script.Setup2();

                        // Build all the steps.
                        int sectionTime = 0;
                        foreach(NSection section in _script.Sections)
                        {
                            foreach((NChannel ch, NSequence seq, int beat) v in section)
                            {
                                // Check for skip/mute.
                                if(v.seq != null)
                                {
                                    _script.AddSequence(v.ch, v.seq, sectionTime + v.beat);
                                }
                            }

                            // Update accumulated time.
                            sectionTime += section.Beats;
                        }

                        ProcessRuntime();

                        SetSpeedTimerPeriod();

                        // Show everything.
                        InitScriptUi();
                    }
                    catch (Exception ex)
                    {
                        ProcessScriptRuntimeError(ex);
                        ok = false;
                    }

                    SetCompileStatus(ok);
                }
                else
                {
                    _logger.Error("Compile failed.");
                    ok = false;
                    ProcessPlay(PlayCommand.StopRewind, false);
                    SetCompileStatus(false);
                }

                _compileResults.ForEach(r =>
                {
                    if (r.ErrorType == ScriptErrorType.Warning)
                    {
                        _logger.Warn(r.ToString());
                    }
                    else
                    {
                        _logger.Error(r.ToString());
                    }
                });
            }

            return ok;
        }

        /// <summary>
        /// Create the main UI parts from the script.
        /// </summary>
        void InitScriptUi()
        {
            ///// Set up UI.
            const int CONTROL_SPACING = 10;
            int x = timeMaster.Right + CONTROL_SPACING;

            ///// The channel controls.
            
            // Clean up current controls.
            foreach (Control ctl in Controls)
            {
                if (ctl is ChannelControl)
                {
                    ChannelControl tctl = ctl as ChannelControl;
                    tctl.ChannelChangeEvent -= ChannelChange_Event;
                    tctl.Dispose();
                    Controls.Remove(tctl);
                }
            }

            if(_script != null)
            {
                foreach (NChannel t in _script.Channels)
                {
                    // Init from persistence.
                    double vt = Convert.ToDouble(_nppVals.GetValue(t.Name, "volume"));
                    t.Volume = vt == 0.0 ? 0.5 : vt; // in case it's new

                    ChannelControl tctl = new ChannelControl()
                    {
                        Location = new Point(x, timeMaster.Top),
                        BoundChannel = t
                    };
                    tctl.ChannelChangeEvent += ChannelChange_Event;
                    Controls.Add(tctl);
                    x += tctl.Width + CONTROL_SPACING;
                }

                // Levers and meters.
                scriptControls.Init(_script.Levers, _script.Displays);

                // Calc the section times.
                timeMaster.TimeDefs.Clear();
                int start = 0;
                foreach (NSection sect in _script.Sections)
                {
                    timeMaster.TimeDefs.Add(start, sect.Name);
                    start += sect.Beats;
                }
                // Add the dummy end marker.
                timeMaster.TimeDefs.Add(start, "");

                if (timeMaster.TimeDefs.Count > 0)
                {
                    timeMaster.MaxBeat = timeMaster.TimeDefs.Keys.Max();
                }
            }

            ///// Init other controls.
            potSpeed.Value = Convert.ToInt32(_nppVals.GetValue("master", "speed"));
            double mv = Convert.ToDouble(_nppVals.GetValue("master", "volume"));

            sldVolume.Value = mv == 0 ? 90 : mv; // in case it's new
        }

        /// <summary>
        /// Update system statuses.
        /// </summary>
        /// <param name="compileStatus"></param>
        void SetCompileStatus(bool compileStatus)
        {
            if (compileStatus)
            {
                btnCompile.Image = MiscUtils.ColorizeBitmap(btnCompile.Image, UserSettings.TheSettings.IconColor);
                _needCompile = false;
            }
            else
            {
                btnCompile.Image = MiscUtils.ColorizeBitmap(btnCompile.Image, Color.Red);
                _needCompile = true;
            }
        }
        #endregion

        #region Realtime handling
        /// <summary>
        /// Multimedia timer tick handler.
        /// </summary>
        void TimerElapsedEvent(object sender, MmTimerEx.TimerEventArgs e)
        {
            // Do some stats gathering for measuring jitter.
            //if (_tan.Grab())
            //{
            //    _logger.Info($"Midi timing: {_tan.Mean}");
            //}

            // Kick over to main UI thread.
            BeginInvoke((MethodInvoker)delegate ()
            {
                if (_script != null)
                {
                    NextStep(e);
                }
            });
        }

        /// <summary>
        /// Output next time/step.
        /// </summary>
        /// <param name="e">Information about updates required.</param>
        void NextStep(MmTimerEx.TimerEventArgs e)
        {
            ////// Neb steps /////
            InitRuntime();

            if (chkPlay.Checked && e.ElapsedTimers.Contains("NEB") && !_needCompile)
            {
                //_tan.Arm();

                // Update section - only on beats.
                if (timeMaster.TimeDefs.Count > 0 && _stepTime.Tick == 0)
                {
                    if(timeMaster.TimeDefs.ContainsKey(_stepTime.Beat))
                    {
                        // currentSection = _stepTime.Beat;
                    }
                }

                // Kick the script. Note: Need exception handling here to protect from user script errors.
                try
                {
                    _script.Step();
                }
                catch (Exception ex)
                {
                    ProcessScriptRuntimeError(ex);
                }

                //if (_tan.Grab())
                //{
                //    _logger.Info($"NEB tan: {_tan.Mean}");
                //}

                // Process any sequence steps.
                _script.Steps.GetSteps(_stepTime).ForEach(s => PlayStep(s));

                ///// Bump time.
                _stepTime.Advance();

                // Check for end of play. If no steps or not selected, free running mode so always keep going.
                if(timeMaster.TimeDefs.Count() > 1)
                {
                   // Check for end.
                   if (_stepTime.Beat > timeMaster.TimeDefs.Last().Key)
                   {
                       ProcessPlay(PlayCommand.StopRewind, false);
                       _outputs.ForEach(o => o.Value?.Kill()); // just in case
                   }
                }
                // else keep going

                ProcessPlay(PlayCommand.UpdateUiTime, false);
            }

            // Process whatever the script did.
            ProcessRuntime();

            // Process any lingering noteoffs etc.
            _outputs.ForEach(o => o.Value?.Housekeep());
            _inputs.ForEach(i => i.Value?.Housekeep());

            ///// Local common function /////
            void PlayStep(Step step)
            {
                if(_script.Channels.Count > 0)
                {
                    NChannel channel = _script.Channels.Where(t => t.ChannelNumber == step.ChannelNumber).First();

                    // Is it ok to play now?
                    bool _anySolo = _script.Channels.Where(t => t.State == ChannelState.Solo).Count() > 0;
                    bool play = channel != null && (channel.State == ChannelState.Solo || (channel.State == ChannelState.Normal && !_anySolo));

                    if (play)
                    {
                        if (step is StepInternal)
                        {
                            // Note: Need exception handling here to protect from user script errors.
                            try
                            {
                                (step as StepInternal).ScriptFunction();
                            }
                            catch (Exception ex)
                            {
                                ProcessScriptRuntimeError(ex);
                            }
                        }
                        else
                        {
                            if (step.Device is NOutput)
                            {
                                // Maybe tweak values.
                                if (step is StepNoteOn)
                                {
                                    (step as StepNoteOn).Adjust(sldVolume.Value, channel.Volume);
                                }
                                (step.Device as NOutput).Send(step);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Process input event.
        /// Is it a controller? Look it up in the inputs.
        /// If not a ctlr or not found, send to the output, otherwise trigger listeners.
        /// </summary>
        void Device_InputEvent(object sender, DeviceInputEventArgs e)
        {
            BeginInvoke((MethodInvoker)delegate ()
            {
                if (_script != null && e.Step != null)
                {
                    try
                    {
                        bool handled = false; // default

                        if (e.Step is StepNoteOn || e.Step is StepNoteOff)
                        {
                            int chanNum = (e.Step as Step).ChannelNumber;
                            // Dig out the note number. !!! Note sign specifies note on/off.
                            double value = (e.Step is StepNoteOn) ? (e.Step as StepNoteOn).NoteNumber : -(e.Step as StepNoteOff).NoteNumber;
                            handled = ProcessInput(sender as NInput, ScriptDefinitions.TheDefinitions.NoteControl, chanNum, value);
                        }
                        else if (e.Step is StepControllerChange)
                        {
                            // Control change.
                            StepControllerChange scc = e.Step as StepControllerChange;
                            handled = ProcessInput(sender as NInput, scc.ControllerId, scc.ChannelNumber, scc.Value);
                        }

                        ///// Local common function /////
                        bool ProcessInput(NInput input, int ctrlId, int channelNum, double value)
                        {
                            bool ret = false;

                            if(input != null)
                            {
                                // Run through our list of inputs of interest.
                                foreach (NController ctlpt in _script.Controllers)
                                {
                                    if (ctlpt.Device == input && ctlpt.ControllerId == ctrlId && ctlpt.ChannelNumber == channelNum)
                                    {
                                        // Assign new value which triggers script callback.
                                        ctlpt.BoundVar.Value = value;
                                        ret = true;
                                    }
                                }
                            }

                            return ret;
                        }

                        if (!handled)
                        {
                            // Pass through. Not.... let the script handle it.
                            //e.Step.Device.Send(e.Step);
                        }
                    }
                    catch (Exception ex)
                    {
                        ProcessScriptRuntimeError(ex);
                    }
                }
            });
        }

        /// <summary>
        /// Process midi log event.
        /// </summary>
        void Device_LogEvent(object sender, DeviceLogEventArgs e)
        {
            BeginInvoke((MethodInvoker)delegate ()
            {
                // Route all events through log.

                switch (e.DeviceLogCategory)
                {
                    case DeviceLogCategory.Error:
                        _logger.Error($"{_stepTime} {e.Message}");
                        break;

                    case DeviceLogCategory.Info:
                        _logger.Info($"{_stepTime} {e.Message}");
                        break;

                    case DeviceLogCategory.Recv:
                        if (UserSettings.TheSettings.MonitorInput)
                        {
                            _logger.Info($"RCV: {_stepTime} {e.Message}");
                        }
                        break;

                    case DeviceLogCategory.Send:
                        if (UserSettings.TheSettings.MonitorOutput)
                        {
                            _logger.Info($"SND: {_stepTime} {e.Message}");
                        }
                        break;
                }
            });
        }

        /// <summary>
        /// User has changed a channel value. Interested in solo/mute and volume.
        /// </summary>
        void ChannelChange_Event(object sender, EventArgs e)
        {
            ChannelControl ch = sender as ChannelControl;
            _nppVals.SetValue(ch.BoundChannel.Name, "volume", ch.BoundChannel.Volume);

            // Check for solos.
            bool _anySolo = _script.Channels.Where(c => c.State == ChannelState.Solo).Count() > 0;

            if (_anySolo)
            {
                // Kill any not solo.
                _script.Channels.ForEach(c =>
                {
                    if (c.State != ChannelState.Solo && c.Device != null)
                    {
                        c.Device.Kill(c.ChannelNumber);
                    }
                });
            }
        }

        /// <summary>
        /// Package up the runtime stuff the script may need. Call this before any script updates.
        /// </summary>
        void InitRuntime()
        {
            _script.Playing = chkPlay.Checked;
            _script.StepTime = _stepTime;
            _script.RealTime = (DateTime.Now - _startTime).TotalSeconds;
            _script.Speed = potSpeed.Value;
            _script.Volume = sldVolume.Value;
        }

        /// <summary>
        /// Process whatever the script may have done.
        /// </summary>
        void ProcessRuntime()
        {
            if (_script.Speed != potSpeed.Value)
            {
                potSpeed.Value = _script.Speed;
                SetSpeedTimerPeriod();
            }

            if (_script.Volume != sldVolume.Value)
            {
                sldVolume.Value = _script.Volume;
            }
        }

        /// <summary>
        /// Runtime error. Look for ones generated by our script - normal occurrence which the user should know about.
        /// </summary>
        /// <param name="ex"></param>
        ScriptError ProcessScriptRuntimeError(Exception ex)
        {
            ProcessPlay(PlayCommand.Stop, false);
            SetCompileStatus(false);

            ScriptError err = null;

            // Locate the offending frame.
            string srcFile = Utils.UNKNOWN_STRING;
            int srcLine = -1;
            StackTrace st = new StackTrace(ex, true);
            StackFrame sf = null;

            for (int i = 0; i < st.FrameCount; i++)
            {
                StackFrame stf = st.GetFrame(i);
                if (stf.GetFileName() != null && stf.GetFileName().ToUpper().Contains(_compileTempDir.ToUpper()))
                {
                    sf = stf;
                    break;
                }
            }

            if (sf != null)
            {
                // Dig out generated file parts.
                string genFile = sf.GetFileName();
                int genLine = sf.GetFileLineNumber() - 1;

                // Open the generated file and dig out the source file and line.
                string[] genLines = File.ReadAllLines(genFile);

                srcFile = genLines[0].Trim().Replace("//", "");

                int ind = genLines[genLine].LastIndexOf("//");
                if (ind != -1)
                {
                    string sl = genLines[genLine].Substring(ind + 2);
                    int.TryParse(sl, out srcLine);
                }

                err = new ScriptError()
                {
                    ErrorType = ScriptErrorType.Runtime,
                    SourceFile = srcFile,
                    LineNumber = srcLine,
                    Message = ex.Message
                };
            }
            else // unknown?
            {
                err = new ScriptError()
                {
                    ErrorType = ScriptErrorType.Runtime,
                    SourceFile = "",
                    LineNumber = -1,
                    Message = ex.Message
                };
            }

            if (err != null)
            {
                _logger.Error(err.ToString());
            }

            return err;
        }
        #endregion

        #region File handling
        /// <summary>
        /// The user has asked to open a recent file.
        /// </summary>
        void Recent_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            string fn = sender.ToString();
            string sopen = OpenFile(fn);
            if (sopen != "")
            {
                _logger.Error(sopen);
            }
        }

        /// <summary>
        /// Allows the user to select a np file from file system.
        /// </summary>
        void Open_Click(object sender, EventArgs e)
        {
            OpenFileDialog openDlg = new OpenFileDialog()
            {
                Filter = "Nebulator files (*.neb)|*.neb",
                Title = "Select a Nebulator file"
            };

            if (openDlg.ShowDialog() == DialogResult.OK)
            {
                string sopen = OpenFile(openDlg.FileName);
                if (sopen != "")
                {
                    _logger.Error(sopen);
                }
            }
        }

        /// <summary>
        /// Common np file opener.
        /// </summary>
        /// <param name="fn">The np file to open.</param>
        /// <returns>Error string or empty if ok.</returns>
        public string OpenFile(string fn)
        {
            string ret = "";

            using (new WaitCursor())
            {
                try
                {
                    SaveProjectValues();

                    if (File.Exists(fn))
                    {
                        _logger.Info($"Opening {fn}");
                        _nppVals = Bag.Load(fn.Replace(".neb", ".nebp"));
                        _fn = fn;

                        // This may be coming from the web service...
                        Invoke((MethodInvoker)delegate
                        {
                            // Running on the UI thread.
                            SetCompileStatus(true);
                            AddToRecentDefs(fn);
                            bool ok = Compile();
                            SetCompileStatus(ok);
                        });

                        Text = $"Nebulator {MiscUtils.GetVersionString()} - {fn}";
                    }
                    else
                    {
                        ret = $"Invalid file: {fn}";
                    }
                }
                catch (Exception ex)
                {
                    ret = $"Couldn't open the np file: {fn} because: {ex.Message}";
                    _logger.Error(ret);
                }
            }

            return ret;
        }

        /// <summary>
        /// Create the menu with the recently used files.
        /// </summary>
        void PopulateRecentMenu()
        {
            ToolStripItemCollection menuItems = recentToolStripMenuItem.DropDownItems;
            menuItems.Clear();

            UserSettings.TheSettings.RecentFiles.ForEach(f =>
            {
                ToolStripMenuItem menuItem = new ToolStripMenuItem(f, null, new EventHandler(Recent_Click));
                menuItems.Add(menuItem);
            });
        }

        /// <summary>
        /// Update the mru with the user selection.
        /// </summary>
        /// <param name="fn">The selected file.</param>
        void AddToRecentDefs(string fn)
        {
            if (File.Exists(fn))
            {
                UserSettings.TheSettings.RecentFiles.UpdateMru(fn);
                PopulateRecentMenu();
            }
        }

        /// <summary>
        /// One or more np files have changed so reload/compile.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void Watcher_Changed(object sender, MultiFileWatcher.FileChangeEventArgs e)
        {
            // Kick over to main UI thread.
            BeginInvoke((MethodInvoker)delegate ()
            {
                SetCompileStatus(false);
            });
        }

        /// <summary>
        /// Self documenting.
        /// </summary>
        void SaveProjectValues()
        {
            _nppVals.Clear();
            _nppVals.SetValue("master", "volume", sldVolume.Value);
            _nppVals.SetValue("master", "speed", potSpeed.Value);
            _script?.Channels?.ForEach(c => _nppVals.SetValue(c.Name, "volume", c.Volume));
            _nppVals.Save();
        }
        #endregion

        #region Main toolbar controls
        /// <summary>
        /// Go or stop button.
        /// </summary>
        void Play_Click(object sender, EventArgs e)
        {
            ProcessPlay(chkPlay.Checked ? PlayCommand.Start : PlayCommand.Stop, true);
        }

        /// <summary>
        /// Update multimedia timer period.
        /// </summary>
        void Speed_ValueChanged(object sender, EventArgs e)
        {
            _nppVals.SetValue("master", "speed", potSpeed.Value);
            SetSpeedTimerPeriod();
        }

        /// <summary>
        /// Go back jack.
        /// </summary>
        void Rewind_Click(object sender, EventArgs e)
        {
            ProcessPlay(PlayCommand.Rewind, true);
        }

        /// <summary>
        /// User updated volume.
        /// </summary>
        void Volume_ValueChanged(object sender, EventArgs e)
        {
            _nppVals.SetValue("master", "volume", sldVolume.Value);
        }

        /// <summary>
        /// Manual recompile.
        /// </summary>
        void Compile_Click(object sender, EventArgs e)
        {
            Compile();
            ProcessPlay(PlayCommand.StopRewind, true);
        }

        /// <summary>
        /// User updated the time.
        /// </summary>
        void Time_ValueChanged(object sender, EventArgs e)
        {
            _stepTime = timeMaster.CurrentTime;
            ProcessPlay(PlayCommand.UpdateUiTime, true);
        }

        /// <summary>
        /// Monitor comm messages. Note that monitoring slows down processing so use judiciously.
        /// </summary>
        void Mon_Click(object sender, EventArgs e)
        {
            UserSettings.TheSettings.MonitorInput = btnMonIn.Checked;
            UserSettings.TheSettings.MonitorOutput = btnMonOut.Checked;
        }

        /// <summary>
        /// The meaning of life.
        /// </summary>
        void About_Click(object sender, EventArgs e)
        {
            // Make some markdown.
            List<string> mdText = new List<string>
            {
                // Device info.
                "# Your Devices",
                "## Midi Input"
            };

            if (MidiIn.NumberOfDevices > 0)
            {
                for (int device = 0; device < MidiIn.NumberOfDevices; device++)
                {
                    mdText.Add($"- {MidiIn.DeviceInfo(device).ProductName}");
                }
            }
            else
            {
                mdText.Add($"- None");
            }
            mdText.Add("## Midi Output");
            if (MidiOut.NumberOfDevices > 0)
            {
                for (int device = 0; device < MidiOut.NumberOfDevices; device++)
                {
                    mdText.Add($"- {MidiOut.DeviceInfo(device).ProductName}");
                }
            }
            else
            {
                mdText.Add($"- None");
            }

            mdText.Add("## Asio");
            if (AsioOut.GetDriverNames().Count() > 0)
            {
                foreach (string sdev in AsioOut.GetDriverNames())
                {
                    mdText.Add($"- {sdev}");
                }
            }
            else
            {
                mdText.Add($"- None");
            }

            // Main help file.
            mdText.Add(File.ReadAllText(@"Resources\README.md"));

            // Put it together.
            List<string> htmlText = new List<string>
            {
                // Boilerplate
                $"<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
                // CSS
                $"<style>body {{ background-color: {UserSettings.TheSettings.BackColor.Name}; font-family: \"Arial\", Helvetica, sans-serif; }}",
                $"</style></head><body>"
            };

            // Meat.
            string mdHtml = string.Join(Environment.NewLine, mdText);
            htmlText.Add(mdHtml);

            // Bottom.
            string ss = "<!-- Markdeep: --><style class=\"fallback\">body{visibility:hidden;white-space:pre;font-family:monospace}</style><script src=\"markdeep.min.js\" charset=\"utf-8\"></script><script src=\"https://casual-effects.com/markdeep/latest/markdeep.min.js\" charset=\"utf-8\"></script><script>window.alreadyProcessedMarkdeep||(document.body.style.visibility=\"visible\")</script>";
            htmlText.Add(ss);
            htmlText.Add($"</body></html>");

            string fn = Path.Combine(Path.GetTempPath(), "nebulator.html");
            File.WriteAllText(fn, string.Join(Environment.NewLine, htmlText));
            Process.Start(fn);
        }
        #endregion

        #region Messages and logging
        /// <summary>
        /// Init all logging functions.
        /// </summary>
        void InitLogging()
        { 
            string appDir = MiscUtils.GetAppDataDir("Nebulator");

            FileInfo fi = new FileInfo(Path.Combine(appDir, "log.txt"));
            if(fi.Exists && fi.Length > 100000)
            {
                File.Copy(fi.FullName, fi.FullName.Replace("log.", "log2."), true);
                File.Delete(fi.FullName);
            }

            // Hook to client window.
            LogClientNotificationTarget.ClientNotification += Log_ClientNotification;
        }

        /// <summary>
        /// A message from the logger to display to the user.
        /// </summary>
        /// <param name="msg">The message.</param>
        void Log_ClientNotification(string msg)
        {
            BeginInvoke((MethodInvoker)delegate ()
            {
                textViewer.AddLine(msg);
            });
        }

        /// <summary>
        /// Show the log file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void LogShow_Click(object sender, EventArgs e)
        {
            using (Form f = new Form()
            {
                Text = "Log Viewer",
                Size = new Size(900, 600),
                Font = UserSettings.TheSettings.EditorFont,
                BackColor = UserSettings.TheSettings.BackColor,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20),
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                ShowIcon = false,
                ShowInTaskbar = false
            })
            {
                TextViewer tv = new TextViewer()
                {
                    Dock = DockStyle.Fill,
                    WordWrap = true
                };

                f.Controls.Add(tv);
                tv.Colors.Add(" ERROR ", Color.Plum);
                tv.Colors.Add(" _WARN ", Color.LightPink);
                //tv.Colors.Add(" SND:", Color.LightGreen);

                string appDir = MiscUtils.GetAppDataDir("Nebulator");
                string logFilename = Path.Combine(appDir, "log.txt");
                using (new WaitCursor())
                {
                    File.ReadAllLines(logFilename).ForEach(l => tv.AddLine(l));
                }

                f.ShowDialog();
            }
        }
        #endregion

        #region User settings
        /// <summary>
        /// Save user settings that aren't automatic.
        /// </summary>
        void SaveSettings()
        {
            UserSettings.TheSettings.MainFormInfo.FromForm(this);
            UserSettings.TheSettings.Save();
        }

        /// <summary>
        /// Edit the common options in a property grid.
        /// </summary>
        void UserSettings_Click(object sender, EventArgs e)
        {
            using (Form f = new Form()
            {
                Text = "User Settings",
                Size = new Size(350, 400),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(200, 200),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowIcon = false,
                ShowInTaskbar = false
            })
            {
                PropertyGridEx pg = new PropertyGridEx()
                {
                    Dock = DockStyle.Fill,
                    PropertySort = PropertySort.NoSort,
                    SelectedObject = UserSettings.TheSettings
                };

                // Detect changes of interest.
                bool ctrls = false;
                pg.PropertyValueChanged += (sdr, args) =>
                {
                    string p = args.ChangedItem.PropertyDescriptor.Name;
                    ctrls |= (p.Contains("Font") | p.Contains("Color"));
                };

                f.Controls.Add(pg);
                f.ShowDialog();

                // Figure out what changed - each handled differently.
                if (ctrls)
                {
                    MessageBox.Show("UI changes require a restart to take effect.");
                }

                SaveSettings();
            }
        }
        #endregion

        #region Play control
        /// <summary>
        /// Update UI state per param.
        /// </summary>
        /// <param name="cmd">The command.</param>
        /// <param name="userAction">Something the user did.</param>
        /// <returns>Indication of success.</returns>
        bool ProcessPlay(PlayCommand cmd, bool userAction)
        {
            bool ret = true;

            switch (cmd)
            {
                case PlayCommand.Start:
                    if (userAction)
                    {
                        bool ok = _needCompile ? Compile() : true;
                        if (ok)
                        {
                            _startTime = DateTime.Now;
                            chkPlay.Checked = true;
                        }
                        else
                        {
                            chkPlay.Checked = false;
                            ret = false;
                        }
                    }
                    else // from the server
                    {
                        if (_needCompile)
                        {
                            ret = false; // not yet
                        }
                        else
                        {
                            _startTime = DateTime.Now;
                            chkPlay.Checked = true;
                        }
                    }
                    break;

                case PlayCommand.Stop:
                    chkPlay.Checked = false;

                    // Send midi stop all notes just in case.
                    _outputs.ForEach(o => o.Value?.Kill());
                    break;

                case PlayCommand.Rewind:
                    _stepTime.Reset();
                    break;

                case PlayCommand.StopRewind:
                    chkPlay.Checked = false;
                    _stepTime.Reset();
                    break;

                case PlayCommand.UpdateUiTime:
                    // See below.
                    break;
            }

            // Always do this.
            timeMaster.CurrentTime = _stepTime;

            _outputs.Values.ForEach(o => { if (chkPlay.Checked) o.Start(); else o.Stop(); });

            return ret;
        }
        #endregion

        #region Keyboard handling
        /// <summary>
        /// Do some global key handling. Space bar is used for stop/start playing.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if(e.KeyCode == Keys.Space)
            {
                // Handle start/stop toggle.
                ProcessPlay(chkPlay.Checked ? PlayCommand.Stop : PlayCommand.Start, true);
                e.Handled = true;
            }
        }
        #endregion

        #region Timers
        /// <summary>
        /// Common func.
        /// </summary>
        void SetSpeedTimerPeriod()
        {
            double secPerBeat = 60 / potSpeed.Value;
            double msecPerTick = 1000 * secPerBeat / Time.TICKS_PER_BEAT;
            _timer.SetTimer("NEB", (int)msecPerTick);
        }
        #endregion

        #region Midi utilities
        /// <summary>
        /// Export steps to a midi file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ExportMidi_Click(object sender, EventArgs e)
        {
            SaveFileDialog saveDlg = new SaveFileDialog()
            {
                Filter = "Midi files (*.mid)|*.mid",
                Title = "Export to midi file",
                FileName = Path.GetFileName(_fn.Replace(".neb", ".mid"))
            };

            if (saveDlg.ShowDialog() == DialogResult.OK)
            {
                ExportMidi(saveDlg.FileName);
            }
        }

        /// <summary>
        /// Export a midi file.
        /// </summary>
        /// <param name="fn">Output filename.</param>
        void ExportMidi(string fn)
        {
            bool ok = true;

            if(_script != null)
            {
                if(_needCompile)
                {
                    ok = Compile();
                }
            }
            else
            {
                ok = false;
            }

            if(ok)
            {
                Dictionary<int, string> channels = new Dictionary<int, string>();
                _script.Channels.ForEach(t => channels.Add(t.ChannelNumber, t.Name));

                // Convert bpm to sec per beat.
                double beatsPerSec = potSpeed.Value / 60;
                double secPerBeat = 1 / beatsPerSec;

                MidiUtils.ExportMidi(_script.Steps, fn, channels, secPerBeat, "Converted from " + _fn);
            }
        }

        /// <summary>
        /// Import a midi or style file as neb file lines.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ImportMidi_Click(object sender, EventArgs e)
        {
            OpenFileDialog openDlg = new OpenFileDialog()
            {
                Filter = "Midi files (*.mid)|*.mid|Style files (*.sty)|*.sty|All files (*.*)|*.*",
                Title = "Import from midi or style file"
            };

            if (openDlg.ShowDialog() == DialogResult.OK)
            {
                var v = MidiUtils.ImportFile(openDlg.FileName);
                Clipboard.SetText(string.Join(Environment.NewLine, v));
                MessageBox.Show("File content is in the clipboard");
            }
        }

        /// <summary>
        /// Kill em all.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void Kill_Click(object sender, EventArgs e)
        {
            _outputs.ForEach(o => o.Value?.Kill());
        }
        #endregion
    }
}
