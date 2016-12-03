﻿namespace Unosquare.RaspberryIO
{
    using System;
    using System.Linq;
    using System.Security;

    /// <summary>
    /// Represents a GPIO Pin, its location and its capabilities.
    /// Full pin reference avaliable here:
    /// http://pinout.xyz/pinout/pin31_gpio6 and  http://wiringpi.com/pins/
    /// </summary>
    public sealed partial class GpioPin
    {
        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="GpioPin"/> class.
        /// </summary>
        /// <param name="wiringPiPinNumber">The wiring pi pin number.</param>
        /// <param name="headerPinNumber">The header pin number.</param>
        private GpioPin(WiringPiPin wiringPiPinNumber, int headerPinNumber)
        {
            PinNumber = (int)wiringPiPinNumber;
            WiringPiPinNumber = wiringPiPinNumber;
            BcmPinNumber = Utilities.WiringPiToBcmPinNumber((int)wiringPiPinNumber);
            HeaderPinNumber = headerPinNumber;
            Header = (PinNumber >= 17 && PinNumber <= 20) ?
                GpioHeader.P5 : GpioHeader.P1;
        }

        #endregion

        #region Property Backing

        private GpioPinDriveMode m_PinMode;
        private GpioPinResistorPullMode m_ResistorPullMode;
        private int m_PwmRegister = 0;
        private PwmMode m_PwmMode = PwmMode.Balanced;
        private uint m_PwmRange = 1024;
        private int m_PwmClockDivisor = 1;

        #endregion

        #region Pin Properties

        /// <summary>
        /// Gets or sets the Wiring Pi pin number as an integer.
        /// </summary>
        public int PinNumber { get; private set; }
        /// <summary>
        /// Gets the WiringPi Pin number
        /// </summary>
        public WiringPiPin WiringPiPinNumber { get; private set; }
        /// <summary>
        /// Gets the BCM chip (hardware) pin number.
        /// </summary>
        public int BcmPinNumber { get; private set; }
        /// <summary>
        /// Gets or the physical header (physical board) pin number.
        /// </summary>
        public int HeaderPinNumber { get; private set; }
        /// <summary>
        /// Gets the pin's header (physical board) location.
        /// </summary>
        public GpioHeader Header { get; private set; }
        /// <summary>
        /// Gets the friendly name of the pin.
        /// </summary>
        public string Name { get; private set; }
        /// <summary>
        /// Gets the hardware mode capabilities of this pin.
        /// </summary>
        public PinCapability[] Capabilities { get; private set; }

        #endregion

        #region Hardware-Specific Properties

        /// <summary>
        /// Gets or sets the pin operating mode.
        /// </summary>
        /// <value>
        /// The pin mode.
        /// </value>
        /// <exception cref="System.NotSupportedException"></exception>
        public GpioPinDriveMode PinMode
        {
            get { return m_PinMode; }
            set
            {
                lock (Pi.SyncLock)
                {
                    var mode = value;
                    if ((mode == GpioPinDriveMode.GpioClock && Capabilities.Contains(PinCapability.GPCLK) == false) ||
                        (mode == GpioPinDriveMode.PwmOutput && Capabilities.Contains(PinCapability.PWM) == false) ||
                        (mode == GpioPinDriveMode.Input && Capabilities.Contains(PinCapability.GP) == false) ||
                        (mode == GpioPinDriveMode.Output && Capabilities.Contains(PinCapability.GP) == false))
                        throw new NotSupportedException($"Pin {WiringPiPinNumber} '{Name}' does not support mode '{mode}'. Pin capabilities are limited to: {string.Join(", ", Capabilities)}");

                    Interop.pinMode(PinNumber, (int)mode);
                    m_PinMode = mode;
                }
            }
        }

        #endregion

        #region Output Mode (Write) Members

        /// <summary>
        /// Writes the specified pin value.
        /// This method performs a digital write
        /// </summary>
        /// <param name="value">The value.</param>
        public void Write(GpioPinValue value)
        {
            lock (Pi.SyncLock)
            {
                if (PinMode != GpioPinDriveMode.Output)
                    throw new InvalidOperationException($"Unable to write to pin {PinNumber} because operating mode is {PinMode}."
                        + $" Writes are only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.Output}");

                Interop.digitalWrite(PinNumber, (int)value);
            }
        }

        /// <summary>
        /// Writes the specified bit value.
        /// This method performs a digital write
        /// </summary>
        /// <param name="value">if set to <c>true</c> [value].</param>
        public void Write(bool value)
        {
            Write(value ? GpioPinValue.High : GpioPinValue.Low);
        }

        /// <summary>
        /// Writes the specified value. 0 for low, any other value for high
        /// This method performs a digital write
        /// </summary>
        /// <param name="value">The value.</param>
        public void Write(int value)
        {
            Write(value != 0 ? GpioPinValue.High : GpioPinValue.Low);
        }

        /// <summary>
        /// Writes the specified value as an analog level.
        /// You will need to register additional analog modules to enable this function for devices such as the Gertboard.
        /// </summary>
        /// <param name="value">The value.</param>
        public void WriteLevel(int value)
        {
            lock (Pi.SyncLock)
            {
                if (PinMode != GpioPinDriveMode.Output)
                    throw new InvalidOperationException($"Unable to write to pin {PinNumber} because operating mode is {PinMode}."
                        + $" Writes are only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.Output}");

                Interop.analogWrite(PinNumber, value);
            }
        }

        #endregion

        #region Input Mode (Read) Members

        /// <summary>
        /// This sets or gets the pull-up or pull-down resistor mode on the pin, which should be set as an input. 
        /// Unlike the Arduino, the BCM2835 has both pull-up an down internal resistors. 
        /// The parameter pud should be; PUD_OFF, (no pull up/down), PUD_DOWN (pull to ground) or PUD_UP (pull to 3.3v) 
        /// The internal pull up/down resistors have a value of approximately 50KΩ on the Raspberry Pi.
        /// </summary>
        public GpioPinResistorPullMode InputPullMode
        {
            get { return PinMode == GpioPinDriveMode.Input ? m_ResistorPullMode : GpioPinResistorPullMode.Off; }
            set
            {
                lock (Pi.SyncLock)
                {
                    if (PinMode != GpioPinDriveMode.Input)
                    {
                        m_ResistorPullMode = GpioPinResistorPullMode.Off;
                        throw new InvalidOperationException($"Unable to set the {nameof(InputPullMode)} for pin {PinNumber} because operating mode is {PinMode}."
                            + $" Setting the {nameof(InputPullMode)} is only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.Input}");
                    }
                    Interop.pullUpDnControl(PinNumber, (int)value);
                    m_ResistorPullMode = value;
                }
            }
        }

        /// <summary>
        /// Reads the digital value on the pin as a boolean value.
        /// </summary>
        /// <returns></returns>
        public bool Read()
        {
            lock (Pi.SyncLock)
            {
                if (PinMode != GpioPinDriveMode.Input)
                    throw new InvalidOperationException($"Unable to read from pin {PinNumber} because operating mode is {PinMode}."
                        + $" Reads are only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.Input}");

                return Interop.digitalRead(PinNumber) == 0 ? false : true;
            }
        }

        /// <summary>
        /// Reads the digital value on the pin as a High or Low value.
        /// </summary>
        /// <returns></returns>
        public GpioPinValue ReadValue()
        {
            return Read() ? GpioPinValue.High : GpioPinValue.Low;
        }

        /// <summary>
        /// Reads the analog value on the pin.
        /// This returns the value read on the supplied analog input pin. You will need to register 
        /// additional analog modules to enable this function for devices such as the Gertboard, 
        /// quick2Wire analog board, etc.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="System.InvalidOperationException"></exception>
        public int ReadLevel()
        {
            lock (Pi.SyncLock)
            {
                if (PinMode != GpioPinDriveMode.Input)
                    throw new InvalidOperationException($"Unable to read from pin {PinNumber} because operating mode is {PinMode}."
                        + $" Reads are only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.Input}");

                return Interop.analogRead(PinNumber);
            }

        }

        #endregion

        #region Hardware PWM Members

        /// <summary>
        /// Gets or sets the PWM register. Values should be between 0 and 1024
        /// </summary>
        /// <value>
        /// The PWM register.
        /// </value>
        public int PwmRegister
        {
            get { return m_PwmRegister; }
            set
            {
                lock (Pi.SyncLock)
                {
                    if (PinMode != GpioPinDriveMode.PwmOutput)
                    {
                        m_PwmRegister = 0;

                        throw new InvalidOperationException($"Unable to write PWM register for pin {PinNumber} because operating mode is {PinMode}."
                            + $" Writing the PWM register is only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.PwmOutput}");
                    }


                    var val = value > 1024 ? 1024 : value;
                    val = value < 0 ? 0 : value;

                    Interop.pwmWrite(PinNumber, val);
                    m_PwmRegister = val;
                }
            }
        }

        /// <summary>
        /// The PWM generator can run in 2 modes – “balanced” and “mark:space”. The mark:space mode is traditional, 
        /// however the default mode in the Pi is “balanced”.
        /// </summary>
        /// <value>
        /// The PWM mode.
        /// </value>
        /// <exception cref="System.InvalidOperationException"></exception>
        public PwmMode PwmMode
        {
            get { return PinMode == GpioPinDriveMode.PwmOutput ? m_PwmMode : PwmMode.Balanced; }
            set
            {
                lock (Pi.SyncLock)
                {
                    if (PinMode != GpioPinDriveMode.PwmOutput)
                    {
                        m_PwmMode = PwmMode.Balanced;

                        throw new InvalidOperationException($"Unable to set PWM mode for pin {PinNumber} because operating mode is {PinMode}."
                            + $" Setting the PWM mode is only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.PwmOutput}");
                    }

                    Interop.pwmSetMode((int)value);
                    m_PwmMode = value;
                }
            }
        }

        /// <summary>
        /// This sets the range register in the PWM generator. The default is 1024.
        /// </summary>
        /// <value>
        /// The PWM range.
        /// </value>
        /// <exception cref="System.InvalidOperationException"></exception>
        public uint PwmRange
        {
            get { return PinMode == GpioPinDriveMode.PwmOutput ? m_PwmRange : 0; }
            set
            {
                lock (Pi.SyncLock)
                {
                    if (PinMode != GpioPinDriveMode.PwmOutput)
                    {
                        m_PwmRange = 1024;

                        throw new InvalidOperationException($"Unable to set PWM range for pin {PinNumber} because operating mode is {PinMode}."
                            + $" Setting the PWM range is only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.PwmOutput}");
                    }

                    Interop.pwmSetRange(value);
                    m_PwmRange = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the PWM clock divisor.
        /// </summary>
        /// <value>
        /// The PWM clock divisor.
        /// </value>
        /// <exception cref="System.InvalidOperationException"></exception>
        public int PwmClockDivisor
        {
            get { return PinMode == GpioPinDriveMode.PwmOutput ? m_PwmClockDivisor : 0; }
            set
            {
                lock (Pi.SyncLock)
                {
                    if (PinMode != GpioPinDriveMode.PwmOutput)
                    {
                        m_PwmClockDivisor = 1;

                        throw new InvalidOperationException($"Unable to set PWM range for pin {PinNumber} because operating mode is {PinMode}."
                            + $" Setting the PWM range is only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.PwmOutput}");
                    }

                    Interop.pwmSetClock(value);
                    m_PwmClockDivisor = value;
                }
            }
        }

        #endregion

        #region Interrupts

        /// <summary>
        /// Gets the interrupt callback. Returns null if no interrupt
        /// has been registered.
        /// </summary>
        public InterrputServiceRoutineCallback InterruptCallback { get; private set; }

        /// <summary>
        /// Gets the interrupt edge detection mode.
        /// </summary>
        public EdgeDetection InterruptEdgeDetection { get; private set; } = EdgeDetection.EdgeSetup;

        /// <summary>
        /// Registers the interrupt callback on the pin. Pin mode has to be set to Input.
        /// 
        /// </summary>
        /// <param name="edgeDetection">The edge detection.</param>
        /// <param name="callback">The callback.</param>
        /// <exception cref="System.ArgumentException">callback</exception>
        /// <exception cref="System.InvalidOperationException">
        /// An interrupt callback was already registered.
        /// or
        /// RegisterInterruptCallback
        /// </exception>
        /// <exception cref="System.InvalidProgramException"></exception>
        public void RegisterInterruptCallback(EdgeDetection edgeDetection, InterrputServiceRoutineCallback callback)
        {
            if (callback == null)
                throw new ArgumentException($"{nameof(callback)} cannot be null");

            if (InterruptCallback != null)
                throw new InvalidOperationException("An interrupt callback was already registered.");

            if (PinMode != GpioPinDriveMode.Input)
                throw new InvalidOperationException($"Unable to {nameof(RegisterInterruptCallback)} for pin {PinNumber} because operating mode is {PinMode}."
                    + $" Calling {nameof(RegisterInterruptCallback)} is only allowed if {nameof(PinMode)} is set to {GpioPinDriveMode.Input}");

            lock (Pi.SyncLock)
            {
                var registerResult = Interop.wiringPiISR(PinNumber, (int)edgeDetection, callback);
                if (registerResult == 0)
                {
                    InterruptEdgeDetection = edgeDetection;
                    InterruptCallback = callback;
                }
                else
                {
                    throw new InvalidProgramException($"Unable to register the required interrupt. Result was: {registerResult}");
                }

            }
        }

        #endregion

    }
}