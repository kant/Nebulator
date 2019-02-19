using System;
using System.Drawing;
using System.Windows.Forms;
using Nebulator.Common;

namespace Nebulator.Controls
{
    /// <summary>
    /// Pan slider control
    /// </summary>
    public partial class Pan : UserControl
    {
        #region Fields
        private double _value;
        #endregion

        #region Properties
        /// <summary>
        /// The current Pan setting.
        /// </summary>
        public double Value
        {
            get
            {
                return _value;
            }
            set
            {
                _value = Utils.Constrain(value, -1.0, 1.0);
                PanChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        /// <summary>
        /// For styling.
        /// </summary>
        public Color ControlColor { get; set; } = Color.Orange;
        #endregion

        #region Events
        /// <summary>
        /// True when pan value changed.
        /// </summary>
        public event EventHandler PanChanged;
        #endregion

        /// <summary>
        /// Creates a new PanSlider control.
        /// </summary>
        public Pan()
        {
            SetStyle(ControlStyles.DoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            InitializeComponent();
        }

        /// <summary>
        /// Draw control.
        /// </summary>
        protected override void OnPaint(PaintEventArgs pe)
        {
            // Setup.
            //pe.Graphics.Clear(UserSettings.TheSettings.BackColor);
            Brush brush = new SolidBrush(ControlColor);
            //Pen pen = new Pen(ControlColor);

            // Draw border.
            int bw = Utils.BORDER_WIDTH;
            Pen penBorder = new Pen(Color.Black, bw);
            pe.Graphics.DrawRectangle(penBorder, 0, 0, Width - 1, Height - 1);

            // Draw data.
            Rectangle drawArea = Rectangle.Inflate(ClientRectangle, -bw, -bw);
            string panValue;
            if (_value == 0.0)
            {
                pe.Graphics.FillRectangle(brush, (Width / 2) - bw, bw, 2 * bw, Height - 2 * bw);
                panValue = "C";
            }
            else if (_value > 0)
            {
                pe.Graphics.FillRectangle(brush, (Width / 2), bw, (int)((Width / 2) * _value), Height - 2 * bw);
                panValue = $"{_value * 100:F0}%R";
            }
            else
            {
                pe.Graphics.FillRectangle(brush, (int)((Width / 2) * (_value + bw)), bw, (int)((Width / 2) * (0 - _value)), Height - 2 * bw);
                panValue = $"{_value * -100:F0}%L";
            }

            // Draw text.
            StringFormat format = new StringFormat() { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
            pe.Graphics.DrawString(panValue, Font, Brushes.Black, ClientRectangle, format);
        }

        /// <summary>
        /// Handle dragging.
        /// </summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                SetValuePanFromMouse(e.X);
            }
            base.OnMouseMove(e);
        }

        /// <summary>
        /// Handle dragging.
        /// </summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            SetValuePanFromMouse(e.X);
            base.OnMouseDown(e);
        }

        /// <summary>
        /// Calculate position.
        /// </summary>
        /// <param name="x"></param>
        void SetValuePanFromMouse(int x)
        {
            Value = (((double)x / Width) * 2.0f) - 1.0f;
        }
    }
}
