﻿using System;
using System.Collections.Generic;
using System.Linq;
using Nebulator.Common;
using Nebulator.Comm;


namespace Nebulator.Script
{
    /// <summary>
    /// Statistical randomizer for time and volume.
    /// </summary>
    public class Wobbler
    {
        /// <summary>Randomizer.</summary>
        Random _rand = new Random();

        /// <summary>Minimum range for randomizing - 3 sigma.</summary>
        public double RangeLow { get; set; } = 0;

        /// <summary>Maximum range for randomizing - 3 sigma.</summary>
        public double RangeHigh { get; set; } = 0;

        /// <summary>
        /// Return next from standard distribution.
        /// </summary>
        /// <param name="val">Center distribution around this.</param>
        /// <returns>Randomized value or val if ranges are 0 (default).</returns>
        public double Next(double val)
        {
            double newVal = val; // default

            if (RangeLow != 0 || RangeHigh != 0)
            {
                double max = val + RangeHigh;
                double min = val + RangeLow;
                double mean = min + (max - min) / 2;
                double sigma = (max - min) / 3; // 3 sd
                newVal = (int)Utils.NextGaussian(_rand, mean, sigma);
            }
            return newVal;
        }

        /// <summary>
        /// Return next from standard distribution.
        /// </summary>
        /// <param name="val">Center distribution around this.</param>
        /// <returns>Randomized value or val if Min/Max are 0.</returns>
        public Time Next(Time val)
        {
            int newVal = (int)Next(val.TotalTocks);
            return new Time(newVal);
        }
    }
}
