﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MoreLinq;
using Nebulator.Common;
using Nebulator.Comm;


// Nebulator API stuff.

namespace Nebulator.Script
{
    public partial class ScriptCore
    {
        #region User script properties
        /// <summary>Sound is playing.</summary>
        public bool playing { get { return Playing; } }

        /// <summary>Current Nebulator step time.</summary>
        public Time stepTime { get { return StepTime; } }

        /// <summary>Current Nebulator Tick.</summary>
        public int tick { get { return StepTime.Tick; } }

        /// <summary>Current Nebulator Tock.</summary>
        public int tock { get { return StepTime.Tock; } }

        /// <summary>Actual time since start pressed.</summary>
        public float now { get { return RealTime; } }

        /// <summary>Tock subdivision.</summary>
        public int tocksPerTick { get { return Time.TOCKS_PER_TICK; } }

        /// <summary>Nebulator Speed in Ticks per minute (aka bpm).</summary>
        public float speed { get { return Speed; } set { Speed = value; } }

        /// <summary>Nebulator master Volume.</summary>
        public int volume { get { return Volume; } set { Volume = value; } }
        #endregion

        #region Functions that can be overridden in the user script
        /// <summary>Called to iniialize Nebulator stuff.</summary>
        public virtual void setupNeb() { }

        /// <summary>Called every Nebulator Tock.</summary>
        public virtual void step() { }
        #endregion

        #region Script callable functions
        /// <summary>
        /// Create a device input.
        /// </summary>
        /// <param name="commName"></param>
        /// <returns></returns>
        protected NInput createInput(string commName)
        {
            NInput nin = null;
            var ins = Inputs.Where(ci => ci.CommName == commName);
            if(ins.Count() == 0)
            {
                throw new Exception($"Invalid input name: {commName}");
            }
            else
            {
                nin = ins.First();
                nin.Init();
            }
            return nin;
        }

        /// <summary>
        /// Create a device output.
        /// </summary>
        /// <param name="commName"></param>
        /// <returns></returns>
        protected NOutput createOutput(string commName)
        {
            NOutput nout = null;
            var outs = Outputs.Where(ci => ci.CommName == commName);
            if (outs.Count() == 0)
            {
                throw new Exception($"Invalid input name: {commName}");
            }
            else
            {
                nout = outs.First();
                nout.Init();
            }
            return nout;
        }

        /// <summary>
        /// Create a controller input.
        /// </summary>
        /// <param name="input">Comm device.</param>
        /// <param name="channelNum">Which channel.</param>
        /// <param name="controlId">Which</param>
        /// <param name="bound">NVariable</param>
        protected void createController(NInput input, int channelNum, int controlId, NVariable bound)
        {
            controlId = Utils.Constrain(controlId, input.Caps.MinControllerValue, input.Caps.MaxControllerValue);
            NController mp = new NController()
            {
                Input = input,
                ChannelNumber = channelNum,
                ControllerId = controlId,
                BoundVar = bound
            };
            InputControllers.Add(mp);
        }

        /// <summary>
        /// Create a UI lever.
        /// </summary>
        /// <param name="bound"></param>
        protected void createLever(NVariable bound)
        {
            NController lp = new NController() { BoundVar = bound };
            Levers.Add(lp);
        }

        /// <summary>
        /// Normal factory.
        /// </summary>
        /// <param name="name">UI name</param>
        /// <param name="val">Initial value</param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="handler">Optional callback</param>
        protected NVariable createVariable(string name, int val, int min, int max, Action handler = null)
        {
            NVariable nv = new NVariable() { Name = name, Value = val, Min = min, Max = max, Changed = handler };
            Variables.Add(nv);
            return nv;
        }

        /// <summary>
        /// Normal constructor.
        /// </summary>
        /// <param name="length"></param>
        protected NSequence createSequence(int length)
        {
            NSequence nseq = new NSequence() { Length = length };
            Sequences.Add(nseq);
            return nseq;
        }

        /// <summary>
        /// Normal factory.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="start"></param>
        /// <param name="length"></param>
        protected NSection createSection(string name, int start, int length)
        {
            NSection nsec = new NSection() { Name = name, Start = start, Length = length };
            Sections.Add(nsec);
            return nsec;
        }

        /// <summary>
        /// Normal factory.
        /// </summary>
        /// <param name="name">UI name</param>
        /// <param name="output">Comm device.</param>
        /// <param name="channelNum"></param>
        /// <param name="wobvol"></param>
        /// <param name="wobbefore"></param>
        /// <param name="wobafter"></param>
        protected NChannel createChannel(string name, NOutput output, int channelNum, int wobvol = 0, int wobbefore = 0, int wobafter = 0)
        {
            NChannel nt = new NChannel()
            {
                Name = name,
                Output = output,
                ChannelNumber = channelNum,
                WobbleVolume = wobvol,
                WobbleTimeBefore = wobbefore,
                WobbleTimeAfter = wobafter
            };
            Channels.Add(nt);
            return nt;
        }

        /// <summary>Send a note immediately. Respects solo/mute. Adds a note off to play after dur time.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="inote">Note number.</param>
        /// <param name="vol">Note volume. If 0, sends NoteOff instead.</param>
        /// <param name="dur">How long it lasts in Time. 0 means no note off generated. User has to turn it off explicitly.</param>
        public void sendNote(NChannel channel, int inote, int vol, double dur)
        {
            bool _anySolo = Channels.Where(ch => ch.State == ChannelState.Solo).Count() > 0;

            bool play = channel.State == ChannelState.Solo || (channel.State == ChannelState.Normal && !_anySolo);

            if (play)
            {
                int vel = channel.NextVol(vol);
                int notenum = Utils.Constrain(inote, channel.Output.Caps.MinNote, channel.Output.Caps.MaxNote);

                if (vol > 0)
                {
                    StepNoteOn step = new StepNoteOn()
                    {
                        Output = channel.Output,
                        ChannelNumber = channel.ChannelNumber,
                        NoteNumber = notenum,
                        Velocity = vel,
                        VelocityToPlay = vel,
                        Duration = new Time(dur)
                    };

                    step.Adjust(volume, channel.Volume);
                    channel.Output.Send(step);
                }
                else
                {
                    StepNoteOff step = new StepNoteOff()
                    {
                        Output = channel.Output,
                        ChannelNumber = channel.ChannelNumber,
                        NoteNumber = notenum
                    };

                    channel.Output.Send(step);
                }
            }
        }

        /// <summary>Send a note immediately. Respects solo/mute.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="snote">Note string using any form allowed in the script. Requires double quotes in the script.</param>
        /// <param name="vol">Note volume.</param>
        /// <param name="dur">How long it lasts in Time representation. 0 means no note off generated.</param>
        public void sendNote(NChannel channel, string snote, int vol, double dur)
        {
            NSequenceElement note = new NSequenceElement(snote);

            if (note.Notes.Count == 0)
            {
                _logger.Warn($"Invalid note: {snote}");
            }
            else
            {
                note.Notes.ForEach(n => sendNote(channel, n, vol, dur));
            }
        }

        /// <summary>Send a note immediately. Respects solo/mute.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="snote">Note string using any form allowed in the script. Requires double quotes in the script.</param>
        /// <param name="vol">Note volume.</param>
        /// <param name="dur">How long it lasts in Time representation. 0 means no note off generated.</param>
        public void sendNote(NChannel channel, string snote, int vol, Time dur)
        {
            sendNote(channel, snote, vol, dur.AsDouble);
        }

        /// <summary>Send a note on immediately. Respects solo/mute.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="inote">Note number.</param>
        /// <param name="vol">Note volume.</param>
        public void sendNoteOn(NChannel channel, int inote, int vol)
        {
            sendNote(channel, inote, vol, 0.0);
        }

        /// <summary>Send a note off immediately.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="inote">Note number.</param>
        public void sendNoteOff(NChannel channel, int inote)
        {
            sendNote(channel, inote, 0, 0.0);
        }

        /// <summary>Send a controller immediately.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="ctlnum">Controller number.</param>
        /// <param name="val">Controller value.</param>
        public void sendController(NChannel channel, int ctlnum, int val)
        {
            StepControllerChange step = new StepControllerChange()
            {
                Output = channel.Output,
                ChannelNumber = channel.ChannelNumber,
                ControllerId = ctlnum,
                Value = val
            };

            channel.Output.Send(step);
        }

        /// <summary>Send a midi patch immediately.</summary>
        /// <param name="channel"></param>
        /// <param name="patch"></param>
        public void sendPatch(NChannel channel, int patch)
        {
            StepPatch step = new StepPatch()
            {
                Output = channel.Output,
                ChannelNumber = channel.ChannelNumber,
                PatchNumber = patch
            };

            channel.Output.Send(step);
        }

        /// <summary>Send a named sequence.</summary>
        /// <param name="channel">Which channel to send it on.</param>
        /// <param name="seq">Which sequence to send.</param>
        public void playSequence(NChannel channel, NSequence seq)
        {
            StepCollection scoll = ScriptUtils.ConvertToSteps(channel, seq, StepTime.Tick);
            RuntimeSteps.Add(scoll);
        }

        /// <summary>
        /// Add a chord or scale definition.
        /// </summary>
        /// <param name="name">"MY_CHORD"</param>
        /// <param name="parts">"1 4 6 b13"</param>
        protected void createNotes(string name, string parts)
        {
            NoteUtils.AddScriptNoteDef(name, parts);
        }

        /// <summary>Convert the argument into numbered notes.</summary>
        /// <param name="note">Note string using any form allowed in the script.</param>
        /// <returns>Array of notes or empty if invalid.</returns>
        public int[] getNotes(string note)
        {
            List<int> notes = NoteUtils.ParseNoteString(note);
            return notes != null ? notes.ToArray() : new int[0];
        }

        /// <summary>Get an array of scale notes.</summary>
        /// <param name="scale">One of the named scales from ScriptDefinitions.md.</param>
        /// <param name="key">Note name and octave.</param>
        /// <returns>Array of notes or empty if invalid.</returns>
        public int[] getScaleNotes(string scale, string key)
        {
            List<int> notes = NoteUtils.GetScaleNotes(scale, key);
            return notes != null ? notes.ToArray() : new int[0];
        }
        #endregion
    }
}
