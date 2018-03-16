﻿namespace Unosquare.RaspberryIO.Peripherals
{
    using Gpio;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using Unosquare.Swan;

    /// <summary>
    /// Implements a digital infrared sensor using the HX1838 38kHz digital receiver.
    /// It registers an interrupt on the pin and fires data events asynchronously to keep CPU usage low.
    /// Some primitive ideas taken from https://github.com/adafruit/IR-Commander/blob/master/ircommander.pde
    /// and https://github.com/z3t0/Arduino-IRremote/blob/master/IRremote.cpp
    /// </summary>
    public sealed class InfraRedSensorHX1838 : IDisposable
    {
        private bool IsDisposed = false; // To detect redundant calls
        private volatile bool IsInReadInterrupt = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="InfraRedSensorHX1838"/> class.
        /// </summary>
        /// <param name="inputPin">The input pin.</param>
        public InfraRedSensorHX1838(GpioPin inputPin)
        {
            InputPin = inputPin;
            ReadInterruptDoWork();
        }

        /// <summary>
        /// Occurs when a single sensor pulse is available.
        /// </summary>
        public event EventHandler<InfraRedSensorPulseEventArgs> PulseAvailable;

        /// <summary>
        /// Occurs when a data buffer is available.
        /// </summary>
        public event EventHandler<InfraRedSensorDataEventArgs> DataAvailable;

        /// <summary>
        /// Enumerates the different reasons why the reaciver flushed the buffer.
        /// </summary>
        public enum ReceiverFlushReason
        {
            /// <summary>
            /// The idle state
            /// </summary>
            Idle,

            /// <summary>
            /// The overflow state
            /// </summary>
            Overflow,
        }

        /// <summary>
        /// Gets the input pin.
        /// </summary>
        public GpioPin InputPin { get; }

        /// <summary>
        /// Creates a string representing all the pulses passed to this method.
        /// </summary>
        /// <param name="pulses">The pulses.</param>
        /// <param name="groupSize">Number of pulse data to output per line.</param>
        /// <returns>A string representing the pulses</returns>
        public static string DebugPulses(InfraRedPulse[] pulses, int groupSize = 4)
        {
            var builder = new StringBuilder(pulses.Length * 24);
            builder.AppendLine();

            for (var i = 0; i < pulses.Length; i += groupSize)
            {
                var p = pulses[i];
                for (var offset = 0; offset < groupSize; offset++)
                {
                    if (i + offset >= pulses.Length)
                        continue;

                    p = pulses[i + offset];
                    builder.Append($" {(p.Value ? "T" : "F")} {p.DurationUsecs,7} | ");
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    // Dispose of Managed objects
                }

                IsDisposed = true;
            }
        }

        /// <summary>
        /// Reads the interrupt do work.
        /// </summary>
        private void ReadInterruptDoWork()
        {
            // Define some constants
            const long GapUsecs = 5000;
            const long MaxElapsedMicroseconds = 250000;
            const long MinElapsedMicroseconds = 50;
            const int IdleCheckIntervalMilliSecs = 32;
            const int MaxPulseCount = 128;

            // Setup the input pin
            InputPin.PinMode = GpioPinDriveMode.Input;
            InputPin.InputPullMode = GpioPinResistorPullMode.PullUp;

            // Get the timers started!
            var pulseTimer = new Native.HighResolutionTimer();
            var idleTimer = new Native.HighResolutionTimer();

            var pulseBuffer = new List<InfraRedPulse>(MaxPulseCount);
            var syncLock = new object();

            var idleChecker = default(Timer);
            idleChecker = new Timer((s) =>
            {
                if (IsInReadInterrupt)
                    return;

                lock (syncLock)
                {
                    if (idleTimer.ElapsedMicroseconds < GapUsecs || idleTimer.IsRunning == false || pulseBuffer.Count <= 0)
                        return;

                    OnInfraredSensorRawDataAvailable(pulseBuffer.ToArray(), ReceiverFlushReason.Idle);
                    pulseBuffer.Clear();
                    idleTimer.Reset();
                }
            });

            InputPin.RegisterInterruptCallback(EdgeDetection.RisingAndFallingEdges, () =>
            {
                IsInReadInterrupt = true;

                lock (syncLock)
                {
                    idleTimer.Restart();
                    idleChecker.Change(IdleCheckIntervalMilliSecs, IdleCheckIntervalMilliSecs);

                    var currentLength = pulseTimer.ElapsedMicroseconds;
                    var currentValue = InputPin.Read();
                    var pulse = new InfraRedPulse(currentValue, currentLength.Clamp(MinElapsedMicroseconds, MaxElapsedMicroseconds));

                    // Restart for the next bit coming in
                    pulseTimer.Restart();

                    // Do not add an idling pulse
                    if (pulse.DurationUsecs < MaxElapsedMicroseconds)
                    {
                        pulseBuffer.Add(pulse);
                        OnInfraredSensorPulseAvailable(pulse);
                    }

                    if (pulseBuffer.Count >= MaxPulseCount)
                    {
                        OnInfraredSensorRawDataAvailable(pulseBuffer.ToArray(), ReceiverFlushReason.Overflow);
                        pulseBuffer.Clear();
                    }
                }

                IsInReadInterrupt = false;
            });

            // Get the timers started
            pulseTimer.Start();
            idleTimer.Start();
            idleChecker.Change(0, IdleCheckIntervalMilliSecs);
        }

        /// <summary>
        /// Called when a single infrared sensor pulse becomes available.
        /// </summary>
        /// <param name="pulse">The pulse.</param>
        private void OnInfraredSensorPulseAvailable(InfraRedPulse pulse)
        {
            PulseAvailable?.Invoke(this, new InfraRedSensorPulseEventArgs(pulse));
        }

        /// <summary>
        /// Called when an infrared sensor raw data buffer becomes available.
        /// </summary>
        /// <param name="pulses">The pulses.</param>
        /// <param name="state">The state.</param>
        private void OnInfraredSensorRawDataAvailable(InfraRedPulse[] pulses, ReceiverFlushReason state)
        {
            DataAvailable?.Invoke(this, new InfraRedSensorDataEventArgs(pulses, state));
        }

        /// <summary>
        /// Provides decoding Methods for the NEC IR Protocol.
        /// Idea taken from here: https://www.sbprojects.net/knowledge/ir/nec.php
        /// </summary>
        public static class NecDecoder
        {
            private const long ShortPulseLengthMin = 560 - 160;
            private const long ShortPulseLengthMax = 560 + 140;

            private const long BurstSpaceLengthMin = 9000 - 1800;
            private const long BurstSpaceLengthMax = 9000 + 1800;

            private const long BurstMarkLengthMin = 4500 - 600;
            private const long BurstMarkLengthMax = 4500 + 600;

            private const long RepeatPulseLengthMin = 2500 - 500;
            private const long RepeatPulseLengthMax = 2500 + 500;

            private const int MaxBitLength = 32;

            /// <summary>
            /// Decodes the IR pulses according to the NEC protocol.
            /// If the train of pulses does not match the protocol, then it returns a null byte array.
            /// If the train of pulses is a repeat code, then it returns a 4-byte array with all bits set.
            /// </summary>
            /// <param name="pulses">The pulses.</param>
            /// <returns>The decoded bytes</returns>
            public static byte[] DecodePulses(InfraRedPulse[] pulses)
            {
                // check if we have a repeat code
                if (IsRepeatCode(pulses))
                    return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

                // from here: https://www.sbprojects.net/knowledge/ir/nec.php
                var startPulseIndex = -1;

                // The first thing we look for is the ACG (1) which should be around 9ms
                for (var pulseIndex = 0; pulseIndex < pulses.Length; pulseIndex++)
                {
                    var p = pulses[pulseIndex];
                    if (p.Value == true && p.DurationUsecs >= BurstSpaceLengthMin && p.DurationUsecs <= BurstSpaceLengthMax)
                    {
                        startPulseIndex = pulseIndex;
                        break;
                    }
                }

                // Return a null result if we could not find ACG (1)
                if (startPulseIndex == -1)
                    return null;
                else
                    startPulseIndex += 1;

                // Find the first 0 value, 4.5 Millisecond
                for (var pulseIndex = startPulseIndex; pulseIndex < pulses.Length; pulseIndex++)
                {
                    var p = pulses[pulseIndex];
                    startPulseIndex = -1;
                    if (p.Value == false && p.DurationUsecs >= BurstMarkLengthMin && p.DurationUsecs <= BurstMarkLengthMax)
                    {
                        startPulseIndex = pulseIndex;
                        break;
                    }
                }

                // Return a null result if we could not find the start of the train of pulses
                if (startPulseIndex == -1)
                    return null;
                else
                    startPulseIndex += 1;

                // Verify that the last pulse is a space (1) and and it is a short pulse
                var bits = new BitArray(MaxBitLength);
                var bitCount = 0;

                var lastPulse = pulses[pulses.Length - 1];
                if (lastPulse.Value == false || lastPulse.DurationUsecs.IsBetween(ShortPulseLengthMin, ShortPulseLengthMax) == false)
                    return null;

                // preallocate the pulse references
                var p1 = default(InfraRedPulse);
                var p2 = default(InfraRedPulse);

                // parse the bits
                for (var pulseIndex = startPulseIndex; pulseIndex < pulses.Length - 1; pulseIndex += 2)
                {
                    // logical 1 is 1 space (1) and 3 marks (0)
                    // logical 0 is 1 space (1) and 1 Mark (0)
                    p1 = pulses[pulseIndex + 0];
                    p2 = pulses[pulseIndex + 1];

                    // Expect a short Space pulse followed by a Mark pulse of variable length
                    if (p1.Value == true && p2.Value == false && p1.DurationUsecs.IsBetween(ShortPulseLengthMin, ShortPulseLengthMax))
                        bits[bitCount++] = p2.DurationUsecs.IsBetween(ShortPulseLengthMin, ShortPulseLengthMax) ? true : false;

                    if (bitCount >= MaxBitLength)
                        break;
                }

                // Check the message is 4 bytes long
                if (bitCount != 32)
                    return null;

                // Return the result
                var result = new byte[bitCount / 8];
                bits.CopyTo(result, 0);
                return result;
            }

            /// <summary>
            /// Determines whether the set of pulses represent a repeat code
            /// </summary>
            /// <param name="pulses">The pulses.</param>
            /// <returns>
            ///   <c>true</c> if the train of pulses represents an NEC repeat code.
            /// </returns>
            public static bool IsRepeatCode(InfraRedPulse[] pulses)
            {
                if (pulses.Length != 4) return false;
                var trainLength = pulses.Sum(s => s.DurationUsecs);
                if (trainLength.IsBetween(12000, 120000) == false)
                    return false;

                var startPulseIndex = -1;

                // The first thing we look for is the ACG (1) which should be around 9ms
                for (var pulseIndex = 0; pulseIndex < pulses.Length; pulseIndex++)
                {
                    var p = pulses[pulseIndex];
                    if (p.Value == true && p.DurationUsecs >= BurstSpaceLengthMin && p.DurationUsecs <= BurstSpaceLengthMax)
                    {
                        startPulseIndex = pulseIndex;
                        break;
                    }
                }

                if (startPulseIndex == -1)
                    return false;

                if (startPulseIndex + 2 >= pulses.Length)
                    return false;

                // Check the next pulse is a 2.5ms low value
                var p1 = pulses[startPulseIndex + 1];
                if (p1.Value == true || p1.DurationUsecs.IsBetween(RepeatPulseLengthMin, RepeatPulseLengthMax) == false)
                    return false;

                // Check the next pulse is a 560 microsecond high value
                var p2 = pulses[startPulseIndex + 2];
                if (p2.Value == false || p2.DurationUsecs.IsBetween(ShortPulseLengthMin, ShortPulseLengthMax) == false)
                    return false;

                // All checks passed. Looks like it really is a repeat code
                return true;
            }
        }

        /// <summary>
        /// Represents data of an infrared pulse
        /// </summary>
        public class InfraRedPulse
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="InfraRedPulse"/> class.
            /// </summary>
            /// <param name="value">if set to <c>true</c> [value].</param>
            /// <param name="durationUsecs">The duration usecs.</param>
            internal InfraRedPulse(bool value, long durationUsecs)
            {
                Value = value;
                DurationUsecs = durationUsecs;
            }

            /// <summary>
            /// Prevents a default instance of the <see cref="InfraRedPulse"/> class from being created.
            /// </summary>
            private InfraRedPulse()
            {
                // placeholder
            }

            /// <summary>
            /// Gets the signal value
            /// </summary>
            public bool Value { get; }

            /// <summary>
            /// Gets the duration microseconds.
            /// </summary>
            public long DurationUsecs { get; }
        }

        /// <summary>
        /// Represents event arguments for when a receiver buffer is ready to be decoded.
        /// </summary>
        /// <seealso cref="EventArgs" />
        public class InfraRedSensorDataEventArgs : EventArgs
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="InfraRedSensorDataEventArgs" /> class.
            /// </summary>
            /// <param name="pulses">The pulses.</param>
            /// <param name="flushReason">The state.</param>
            internal InfraRedSensorDataEventArgs(InfraRedPulse[] pulses, ReceiverFlushReason flushReason)
            {
                Pulses = pulses;
                FlushReason = flushReason;
                TrainDurationUsecs = pulses.Sum(s => s.DurationUsecs);
            }

            /// <summary>
            /// Gets the array fo IR pulses.
            /// </summary>
            public InfraRedPulse[] Pulses { get; }

            /// <summary>
            /// Gets the state of the receiver that triggered the event.
            /// </summary>
            public ReceiverFlushReason FlushReason { get; }

            /// <summary>
            /// Gets the pulse train duration in microseconds.
            /// </summary>
            public long TrainDurationUsecs { get; }
        }

        /// <summary>
        /// Contains the raw sensor data event arguments
        /// </summary>
        /// <seealso cref="EventArgs" />
        public class InfraRedSensorPulseEventArgs : EventArgs
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="InfraRedSensorPulseEventArgs" /> class.
            /// </summary>
            /// <param name="pulse">The pulse.</param>
            internal InfraRedSensorPulseEventArgs(InfraRedPulse pulse)
                : base()
            {
                Value = pulse.Value;
                DurationUsecs = pulse.DurationUsecs;
            }

            /// <summary>
            /// Prevents a default instance of the <see cref="InfraRedSensorPulseEventArgs"/> class from being created.
            /// </summary>
            private InfraRedSensorPulseEventArgs()
                : base()
            {
                // placeholder
            }

            /// <summary>
            /// Gets the signal value
            /// </summary>
            public bool Value { get; }

            /// <summary>
            /// Gets the duration microseconds.
            /// </summary>
            public long DurationUsecs { get; }
        }
    }
}
