﻿using System;
using System.Collections.Generic;
using NAudio.Midi;
using Nebulator.Common;
using Nebulator.Comm;


namespace Nebulator.Midi
{
    /// <summary>
    /// Abstraction layer between NAudio midi and Nebulator steps. TODO these should be one per input/output....
    /// </summary>
    public class MidiInterface : IComm, IDisposable
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
        public event EventHandler<CommInputEventArgs> CommInputEvent;

        /// <inheritdoc />
        public event EventHandler<CommLogEventArgs> CommLogEvent;
        #endregion

        #region Properties
        /// <inheritdoc />
        public List<string> CommInputs { get; set; } = new List<string>();

        /// <inheritdoc />
        public List<string> CommOutputs { get; set; } = new List<string>();

        /// <inheritdoc />
        public CommCaps Caps { get; set; } = null;
        #endregion

        #region Lifecycle
        /// <summary>
        /// Constructor.
        /// </summary>
        public MidiInterface()
        {
            Caps = new CommCaps()
            {
                NumChannels = 16,
                MinVolume = 0,
                MaxVolume = 127,
                MinNote = 0,
                MaxNote = 127,
                MinControllerValue = 0,
                MaxControllerValue = 127,
                MinPitchValue = 0,
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
        public void Housekeep()
        {
            // Send any stops due.
            _stops.ForEach(s => { s.Expiry--; if (s.Expiry < 0) Send(s); });
            
            // Reset.
            _stops.RemoveAll(s => s.Expiry < 0);
        }
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
                                    Utils.Constrain(stt.NoteNumber, Caps.MinNote, Caps.MaxNote),
                                    Utils.Constrain(stt.VelocityToPlay, Caps.MinVolume, Caps.MaxVolume));
                                msg = evt.GetAsShortMessage();

                                if(stt.Duration.TotalTocks > 0)
                                {
                                    // Remove any lingering note offs and add a fresh one.
                                    _stops.RemoveAll(s => s.NoteNumber == stt.NoteNumber && s.Channel == stt.Channel);

                                    _stops.Add(new StepNoteOff()
                                    {
                                        Channel = stt.Channel,
                                        NoteNumber = Utils.Constrain(stt.NoteNumber, Caps.MinNote, Caps.MaxNote),
                                        Expiry = stt.Duration.TotalTocks
                                    });
                                }
                            }
                            break;

                        case StepNoteOff stt:
                            {
                                NoteEvent evt = new NoteEvent(0, stt.Channel, MidiCommandCode.NoteOff,
                                    Utils.Constrain(stt.NoteNumber, Caps.MinNote, Caps.MaxNote),
                                    Utils.Constrain(stt.Velocity, Caps.MinVolume, Caps.MaxVolume));
                                msg = evt.GetAsShortMessage();
                            }
                            break;

                        case StepControllerChange stt:
                            {
                                if (stt.ControllerId == ScriptDefinitions.TheDefinitions.NoteControl)
                                {
                                    // Shouldn't happen, ignore.
                                }
                                else if (stt.ControllerId == ScriptDefinitions.TheDefinitions.PitchControl)
                                {
                                    PitchWheelChangeEvent pevt = new PitchWheelChangeEvent(0, stt.Channel,
                                        Utils.Constrain(stt.Value, Caps.MinPitchValue, Caps.MaxPitchValue));
                                    msg = pevt.GetAsShortMessage();
                                }
                                else // CC
                                {
                                    ControlChangeEvent nevt = new ControlChangeEvent(0, stt.Channel, (MidiController)stt.ControllerId,
                                        Utils.Constrain(stt.Value, Caps.MinControllerValue, Caps.MaxControllerValue));
                                    msg = nevt.GetAsShortMessage();
                                }
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

                            if (CommSettings.TheSettings.MonitorOutput)
                            {
                                LogMsg(CommLogEventArgs.LogCategory.Send, step.ToString());
                            }
                        }
                        catch (Exception ex)
                        {
                            LogMsg(CommLogEventArgs.LogCategory.Error, $"Midi couldn't send step {step}: {ex.Message}");
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
                            ControllerId = ScriptDefinitions.TheDefinitions.PitchControl,
                            Value = Utils.Constrain(evt.Pitch, Caps.MinPitchValue, Caps.MaxPitchValue)
                        };
                    }
                    break;
            }

            if (step != null)
            {
                // Pass it up for handling.
                CommInputEventArgs args = new CommInputEventArgs() { Step = step };
                CommInputEvent?.Invoke(this, args);

                if (CommSettings.TheSettings.MonitorInput)
                {
                    LogMsg(CommLogEventArgs.LogCategory.Recv, step.ToString());
                }
            }
        }

        /// <summary>
        /// Process error midi event - Parameter 1 is invalid.
        /// </summary>
        void MidiIn_ErrorReceived(object sender, MidiInMessageEventArgs e)
        {
            if (CommSettings.TheSettings.MonitorInput)
            {
                LogMsg(CommLogEventArgs.LogCategory.Error, $"Message:0x{e.RawMessage:X8}");
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

                CommInputs.Clear();
                for (int device = 0; device < MidiIn.NumberOfDevices; device++)
                {
                    CommInputs.Add(MidiIn.DeviceInfo(device).ProductName);
                }

                if (CommInputs.Count > 0 && CommInputs.Contains(CommSettings.TheSettings.InputDevice))
                {
                    _midiIn = new MidiIn(CommInputs.IndexOf(CommSettings.TheSettings.InputDevice));
                    _midiIn.MessageReceived += MidiIn_MessageReceived;
                    _midiIn.ErrorReceived += MidiIn_ErrorReceived;
                    _midiIn.Start();
                }
                else
                {
                    LogMsg(CommLogEventArgs.LogCategory.Info, "No midi input device selected.");
                }
            }
            catch (Exception ex)
            {
                LogMsg(CommLogEventArgs.LogCategory.Error, $"Init midi in failed: {ex.Message}");
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

                CommOutputs.Clear();
                for (int device = 0; device < MidiOut.NumberOfDevices; device++)
                {
                    CommOutputs.Add(MidiOut.DeviceInfo(device).ProductName);
                }

                if (CommOutputs.Count > 0 && CommOutputs.Contains(CommSettings.TheSettings.OutputDevice))
                {
                    int mi = CommOutputs.IndexOf(CommSettings.TheSettings.OutputDevice);
                    _midiOut = new MidiOut(mi);
                }
                else
                {
                    LogMsg(CommLogEventArgs.LogCategory.Error, "No midi output device selected.");
                }
            }
            catch (Exception ex)
            {
                LogMsg(CommLogEventArgs.LogCategory.Error, $"Init midi out failed: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void KillAll()
        {
            for (int i = 0; i < Caps.NumChannels; i++)
            {
                Kill(i + 1);
            }
        }

        /// <inheritdoc />
        public void Kill(int? channel)
        {
            if(channel is null)
            {
                for (int i = 0; i < Caps.NumChannels; i++)
                {
                    Send(new StepControllerChange() { Channel = i + 1, ControllerId = (int)MidiController.AllNotesOff });
                }
            }
            else
            {
                Send(new StepControllerChange() { Channel = channel.Value, ControllerId = (int)MidiController.AllNotesOff });
            }
        }

        /// <summary>Ask host to do something with this.</summary>
        /// <param name="cat"></param>
        /// <param name="msg"></param>
        void LogMsg(CommLogEventArgs.LogCategory cat, string msg)
        {
            CommLogEvent?.Invoke(this, new CommLogEventArgs() { Category = cat, Message = msg });
        }
        #endregion
    }
}
