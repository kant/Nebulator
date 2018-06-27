﻿using System;
using System.Collections.Generic;
using NAudio.Midi;
using Nebulator.Common;
using Nebulator.Protocol;


namespace Nebulator.Midi
{
    /// <summary>
    /// Abstraction layer between NAudio midi and Nebulator steps.
    /// </summary>
    public class MidiInterface : IProtocol, IDisposable
    {
        #region Fields
        /// <summary>Midi input device.</summary>
        MidiIn _midiIn = null;

        /// <summary>Midi output device.</summary>
        MidiOut _midiOut = null;

        /// <summary>Midi access synchronizer.</summary>
        object _midiLock = new object();

        /// <summary>Notes to stop later.</summary>
        List<StepNoteOff> _stops = new List<StepNoteOff>();

        /// <summary>Resource clean up.</summary>
        bool _disposed = false;
        #endregion

        #region Events
        /// <inheritdoc />
        public event EventHandler<ProtocolInputEventArgs> ProtocolInputEvent;

        /// <inheritdoc />
        public event EventHandler<ProtocolLogEventArgs> ProtocolLogEvent;
        #endregion

        #region Properties
        /// <inheritdoc />
        public List<string> ProtocolInputs { get; set; } = new List<string>();

        /// <inheritdoc />
        public List<string> ProtocolOutputs { get; set; } = new List<string>();

        /// <inheritdoc />
        public ProtocolCaps Caps { get; set; } = null;
        #endregion

        #region Lifecycle
        /// <summary>
        /// Constructor.
        /// </summary>
        public MidiInterface()
        {
            Caps = new ProtocolCaps()
            {
                MaxChannels = 16,
                MinVolume = 0,
                MaxVolume = 127,
                MinNote = 0,
                MaxNote = 127,
                MaxControllerValue = 127,
                MaxPitchValue = 16383
            };
        }

        /// <inheritdoc />
        public void Init()
        {
            InitMidiIn();
            InitMidiOut();
        }

        /// <inheritdoc />
        public void Start()
        {
            _midiIn?.Start();
        }

        /// <inheritdoc />
        public void Stop()
        {
            _midiIn?.Stop();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Resource clean up.
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _midiIn?.Stop();
                _midiIn?.Dispose();
                _midiIn = null;

                _midiOut?.Dispose();
                _midiOut = null;

                _disposed = true;
            }
        }
        #endregion

        #region Public methods
        /// <inheritdoc />
        public void Housekeep() // TODO make this common?
        {
            // Send any stops due.
            _stops.ForEach(s => { s.Expiry--; if (s.Expiry < 0) Send(s); });
            
            // Reset.
            _stops.RemoveAll(s => s.Expiry < 0);
        }

        // /// <summary>
        // /// Convert from NAudio def to Neb.
        // /// </summary>
        // /// <param name="sctlr"></param>
        // /// <returns></returns>
        // public static int TranslateController(string sctlr)
        // {
        //     MidiController ctlr = (MidiController)Enum.Parse(typeof(MidiController), sctlr);
        //     return (int)ctlr;
        // }
        #endregion

        #region Midi I/O
        /// <inheritdoc />
        public void Send(Step step)
        {
            // Critical code section
            lock (_midiLock)
            {
                if(_midiOut != null)
                {
                    int msg = 0;

                    switch (step)
                    {
                        case StepNoteOn stt:
                            {
                                NoteEvent evt = new NoteEvent(0, stt.Channel, MidiCommandCode.NoteOn, 
                                    Utils.Constrain(stt.NoteNumberToPlay, Caps.MinNote, Caps.MaxNote),
                                    Utils.Constrain(stt.VelocityToPlay, Caps.MinVolume, Caps.MaxVolume));
                                msg = evt.GetAsShortMessage();

                                if(stt.Duration.TotalTocks > 0)
                                {
                                    // Remove any lingering note offs and add a fresh one.
                                    _stops.RemoveAll(s => s.NoteNumber == stt.NoteNumber);

                                    _stops.Add(new StepNoteOff()
                                    {
                                        Channel = stt.Channel,
                                        NoteNumber = Utils.Constrain(stt.NoteNumber, Caps.MinNote, Caps.MaxNote),
                                        NoteNumberToPlay = stt.NoteNumberToPlay,
                                        Expiry = stt.Duration.TotalTocks
                                    });
                                }
                            }
                            break;

                        case StepNoteOff stt:
                            {
                                NoteEvent evt = new NoteEvent(0, stt.Channel, MidiCommandCode.NoteOff,
                                    Utils.Constrain(stt.NoteNumberToPlay, Caps.MinNote, Caps.MaxNote),
                                    Utils.Constrain(stt.Velocity, Caps.MinVolume, Caps.MaxVolume));
                                msg = evt.GetAsShortMessage();
                            }
                            break;

                        case StepControllerChange stt:
                            {
                                switch (stt.ControllerType)
                                {
                                    case ControllerTypes.Normal:
                                        ControlChangeEvent nevt = new ControlChangeEvent(0, stt.Channel, (MidiController)stt.ControllerId,
                                            Utils.Constrain(stt.Value, 0, Caps.MaxControllerValue));
                                        msg = nevt.GetAsShortMessage();
                                        break;

                                    case ControllerTypes.Pitch:
                                        PitchWheelChangeEvent pevt = new PitchWheelChangeEvent(0, stt.Channel,
                                            Utils.Constrain(stt.Value, 0, Caps.MaxPitchValue));
                                        msg = pevt.GetAsShortMessage();
                                        break;

                                    case ControllerTypes.Note:
                                        // Don't care.
                                        break;
                                }
                                break;
                            }
                            break;

                        case StepPatch stt:
                            {
                                PatchChangeEvent evt = new PatchChangeEvent(0, stt.Channel, stt.PatchNumber);
                                msg = evt.GetAsShortMessage();
                            }
                            break;

                        default:
                            break;
                    }

                    if(msg != 0)
                    {
                        try
                        {
                            _midiOut.Send(msg);

                            if (UserSettings.TheSettings.MidiMonitorOut)
                            {
                                LogSendMsg(step.ToString());
                            }
                        }
                        catch (Exception ex)
                        {
                            LogErrMsg($"Midi couldn't send step {step}: {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Process input midi event. Note that NoteOn with 0 velocity are converted to NoteOff.
        /// </summary>
        void MidiIn_MessageReceived(object sender, MidiInMessageEventArgs e)
        {
            // Decode the message. We only care about a few.
            MidiEvent me = MidiEvent.FromRawMessage(e.RawMessage);
            Step step = null;

            switch (me.CommandCode)
            {
                case MidiCommandCode.NoteOn:
                    {
                        // TODOX test if it's in our controller list - see below.


                        NoteOnEvent evt = me as NoteOnEvent;

                        if(evt.Velocity == 0)
                        {
                            step = new StepNoteOff()
                            {
                                Channel = evt.Channel,
                                NoteNumber = Utils.Constrain(evt.NoteNumber, Caps.MinNote, Caps.MaxNote),
                                Velocity = 0
                            };

                        }
                        else
                        {
                            step = new StepNoteOn()
                            {
                                Channel = evt.Channel,
                                NoteNumber = evt.NoteNumber,
                                NoteNumberToPlay = evt.NoteNumber,
                                Velocity = evt.Velocity,
                                VelocityToPlay = evt.Velocity,
                                Duration = new Time(0)
                            };
                        }
                    }
                    break;

                case MidiCommandCode.NoteOff:
                    {
                        NoteEvent evt = me as NoteEvent;
                        step = new StepNoteOff()
                        {
                            Channel = evt.Channel,
                            NoteNumber = Utils.Constrain(evt.NoteNumber, Caps.MinNote, Caps.MaxNote),
                            Velocity = evt.Velocity
                        };
                    }
                    break;

                case MidiCommandCode.ControlChange:
                    {
                        ControlChangeEvent evt = me as ControlChangeEvent;
                        step = new StepControllerChange()
                        {
                            Channel = evt.Channel,
                            ControllerType = ControllerTypes.Normal,
                            ControllerId = (int)evt.Controller,
                            Value = (byte)evt.ControllerValue
                        };
                    }
                    break;

                case MidiCommandCode.PitchWheelChange:
                    {
                        PitchWheelChangeEvent evt = me as PitchWheelChangeEvent;
                        step = new StepControllerChange()
                        {
                            Channel = evt.Channel,
                            ControllerType = ControllerTypes.Pitch,
                            ControllerId = -1,
                            Value = evt.Pitch
                        };
                    }
                    break;
            }

            if (step != null)
            {
                if(step is StepNoteOn || step is StepNoteOff)
                {
                    //if(channel is in midi note in list) // TODOX
                    //{
                    //    NebMidiInputEvent?.Invoke(this, new NebMidiInputEventArgs() { Step = step });
                    //}
                    //else
                    {
                        // Pass through.
                        Send(step);
                    }
                }
                else
                {
                    // Pass it up for handling.
                    ProtocolInputEvent?.Invoke(this, new ProtocolInputEventArgs() { Step = step });
                }

                // original
                // if(step is StepNoteOn || step is StepNoteOff)
                // {
                //     // Pass through. or do something useful with it: change note, map to controller, etc.
                //     Send(step);
                // }
                // else
                // {
                //     // Pass it up for handling.
                //     NebMidiInputEvent?.Invoke(this, new NebMidiInputEventArgs() { Step = step });
                // }

                if (UserSettings.TheSettings.MidiMonitorIn)
                {
                    LogRcvMsg(step.ToString());
                }
            }
        }

        /// <summary>
        /// Process error midi event - Parameter 1 is invalid.
        /// </summary>
        void MidiIn_ErrorReceived(object sender, MidiInMessageEventArgs e)
        {
            if (UserSettings.TheSettings.MidiMonitorIn)
            {
                LogErrMsg($"Message:0x{e.RawMessage:X8}");
            }
        }

        /// <summary>
        /// Set up midi in.
        /// </summary>
        void InitMidiIn()
        {
            try
            {
                if (_midiIn != null)
                {
                    _midiIn.Stop();
                    _midiIn.Dispose();
                    _midiIn = null;
                }

                ProtocolInputs.Clear();
                for (int device = 0; device < MidiIn.NumberOfDevices; device++)
                {
                    ProtocolInputs.Add(MidiIn.DeviceInfo(device).ProductName);
                }

                if (ProtocolInputs.Count > 0 && ProtocolInputs.Contains(UserSettings.TheSettings.MidiIn))
                {
                    _midiIn = new MidiIn(ProtocolInputs.IndexOf(UserSettings.TheSettings.MidiIn));
                    _midiIn.MessageReceived += MidiIn_MessageReceived;
                    _midiIn.ErrorReceived += MidiIn_ErrorReceived;
                    _midiIn.Start();
                }
                else
                {
                    LogInfoMsg("No midi input device selected.");
                }
            }
            catch (Exception ex)
            {
                LogErrMsg($"Init midi in failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set up midi out.
        /// </summary>
        void InitMidiOut()
        {
            try
            {
                if (_midiOut != null)
                {
                    _midiOut.Dispose();
                    _midiOut = null;
                }

                ProtocolOutputs.Clear();
                for (int device = 0; device < MidiOut.NumberOfDevices; device++)
                {
                    ProtocolOutputs.Add(MidiOut.DeviceInfo(device).ProductName);
                }

                if (ProtocolOutputs.Count > 0 && ProtocolOutputs.Contains(UserSettings.TheSettings.MidiOut))
                {
                    int mi = ProtocolOutputs.IndexOf(UserSettings.TheSettings.MidiOut);
                    _midiOut = new MidiOut(mi);
                    //_midiOut.Volume = -1; needs to be this
                }
                else
                {
                    LogErrMsg("No midi output device selected.");
                }
            }
            catch (Exception ex)
            {
                LogErrMsg($"Init midi out failed: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void KillAll()
        {
            for (int i = 0; i < Caps.MaxChannels; i++)
            {
                Kill(i + 1);
            }
        }

        /// <inheritdoc />
        public void Kill(int channel)
        {
            StepControllerChange step = new StepControllerChange()
            {
                Channel = channel,
                ControllerId = (int)MidiController.AllNotesOff
            };
            Send(step);
        }
        #endregion

        #region Log message helpers
        void LogInfoMsg(string msg)
        {
            ProtocolLogEvent?.Invoke(this, new ProtocolLogEventArgs() { Category = ProtocolLogEventArgs.LogCategory.Info, Message = msg });
        }

        void LogSendMsg(string msg)
        {
            ProtocolLogEvent?.Invoke(this, new ProtocolLogEventArgs() { Category = ProtocolLogEventArgs.LogCategory.Send, Message = msg });
        }

        void LogRcvMsg(string msg)
        {
            ProtocolLogEvent?.Invoke(this, new ProtocolLogEventArgs() { Category = ProtocolLogEventArgs.LogCategory.Recv, Message = msg });
        }

        void LogErrMsg(string msg)
        {
            ProtocolLogEvent?.Invoke(this, new ProtocolLogEventArgs() { Category = ProtocolLogEventArgs.LogCategory.Error, Message = msg });
        }
        #endregion
    }
}
