using System;

namespace StreamHelper
{
    /// <summary>
    /// Calculates descriptive statistics incrementally without buffering
    /// individual samples.
    ///
    /// Regression is performed against the zero-based sample index:
    /// x = 0, 1, 2, ...
    ///
    /// This class uses constant memory regardless of the number of samples.
    /// </summary>
    public class SimpleStreamingStats
    {
        private readonly object _syncRoot = new();

        private long _count;

        // Online central moments.
        private double _mean;
        private double _m2;
        private double _m3;
        private double _m4;

        // Sum of squares of raw values, used for RMS.
        private double _sumSquares;

        private double _minimum;
        private double _maximum;

        // Running regression values.
        private double _meanX;
        private double _meanY;
        private double _sxx;
        private double _syy;
        private double _sxy;

        /// <summary>
        /// Adds one sample to the running statistics.
        /// </summary>
        public void AddValue(double value)
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "The sample must be a finite number.");

            lock (_syncRoot)
            {
                long previousCount = _count;
                _count++;

                if (_count == 1)
                {
                    _minimum = value;
                    _maximum = value;
                }
                else
                {
                    if (value < _minimum)
                        _minimum = value;

                    if (value > _maximum)
                        _maximum = value;
                }

                _sumSquares += value * value;

                UpdateMoments(value, previousCount);
                UpdateRegression(value, previousCount);
            }
        }

        /// <summary>
        /// Clears all accumulated statistics.
        /// </summary>
        public void ResetStats()
        {
            lock (_syncRoot)
            {
                _count = 0;

                _mean = 0.0;
                _m2 = 0.0;
                _m3 = 0.0;
                _m4 = 0.0;

                _sumSquares = 0.0;

                _minimum = 0.0;
                _maximum = 0.0;

                _meanX = 0.0;
                _meanY = 0.0;
                _sxx = 0.0;
                _syy = 0.0;
                _sxy = 0.0;
            }
        }

        public long GetCount()
        {
            lock (_syncRoot)
                return _count;
        }

        public double GetMean()
        {
            lock (_syncRoot)
                return _count > 0 ? _mean : double.NaN;
        }

        /// <summary>
        /// Returns variance using N as the denominator.
        /// </summary>
        public double GetPopulationVariance()
        {
            lock (_syncRoot)
                return _count > 0
                    ? _m2 / _count
                    : double.NaN;
        }

        /// <summary>
        /// Returns variance using N - 1 as the denominator.
        /// </summary>
        public double GetSampleVariance()
        {
            lock (_syncRoot)
                return _count > 1
                    ? _m2 / (_count - 1)
                    : double.NaN;
        }

        public double GetPopulationStandardDeviation()
        {
            lock (_syncRoot)
            {
                return _count > 0
                    ? Math.Sqrt(Math.Max(0.0, _m2 / _count))
                    : double.NaN;
            }
        }

        public double GetSampleStandardDeviation()
        {
            lock (_syncRoot)
            {
                return _count > 1
                    ? Math.Sqrt(Math.Max(0.0, _m2 / (_count - 1)))
                    : double.NaN;
            }
        }

        public double GetMinimum()
        {
            lock (_syncRoot)
                return _count > 0 ? _minimum : double.NaN;
        }

        public double GetMaximum()
        {
            lock (_syncRoot)
                return _count > 0 ? _maximum : double.NaN;
        }

        public double GetRange()
        {
            lock (_syncRoot)
                return _count > 0
                    ? _maximum - _minimum
                    : double.NaN;
        }

        public double GetRootMeanSquare()
        {
            lock (_syncRoot)
            {
                return _count > 0
                    ? Math.Sqrt(_sumSquares / _count)
                    : double.NaN;
            }
        }

        /// <summary>
        /// Returns adjusted sample skewness.
        /// Requires at least three samples.
        /// </summary>
        public double GetSkewness()
        {
            lock (_syncRoot)
            {
                if (_count < 3 || _m2 <= 0.0)
                    return double.NaN;

                double n = _count;

                return Math.Sqrt(n * (n - 1.0))
                       / (n - 2.0)
                       * (_m3 / Math.Pow(_m2, 1.5));
            }
        }

        /// <summary>
        /// Returns bias-corrected excess kurtosis.
        ///
        /// A normal distribution has an excess kurtosis near zero.
        /// Requires at least four samples.
        /// </summary>
        public double GetExcessKurtosis()
        {
            lock (_syncRoot)
            {
                if (_count < 4 || _m2 <= 0.0)
                    return double.NaN;

                double n = _count;

                double rawKurtosis =
                    n * _m4 / (_m2 * _m2);

                return ((n - 1.0) /
                        ((n - 2.0) * (n - 3.0)))
                       *
                       ((n + 1.0) * (rawKurtosis - 3.0) + 6.0);
            }
        }

        /// <summary>
        /// Returns the slope per sample interval.
        ///
        /// For example, if samples are one second apart, this is the slope
        /// per second.
        /// </summary>
        public double GetSlope()
        {
            lock (_syncRoot)
            {
                return _count > 1 && _sxx > 0.0
                    ? _sxy / _sxx
                    : double.NaN;
            }
        }

        /// <summary>
        /// Returns the slope converted to a specified time unit.
        ///
        /// Example:
        /// sampleIntervalSeconds = 0.1
        /// returns units per second.
        /// </summary>
        public double GetSlopePerTime(double sampleInterval)
        {
            if (!double.IsFinite(sampleInterval) || sampleInterval <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(sampleInterval),
                    "Sample interval must be finite and greater than zero.");

            lock (_syncRoot)
            {
                return _count > 1 && _sxx > 0.0
                    ? (_sxy / _sxx) / sampleInterval
                    : double.NaN;
            }
        }

        public double GetIntercept()
        {
            lock (_syncRoot)
            {
                if (_count < 2 || _sxx <= 0.0)
                    return double.NaN;

                double slope = _sxy / _sxx;

                return _meanY - slope * _meanX;
            }
        }

        public double GetCorrelation()
        {
            lock (_syncRoot)
            {
                if (_count < 2 || _sxx <= 0.0 || _syy <= 0.0)
                    return double.NaN;

                double denominator = Math.Sqrt(_sxx * _syy);

                if (denominator <= 0.0)
                    return double.NaN;

                return Math.Clamp(
                    _sxy / denominator,
                    -1.0,
                    1.0);
            }
        }

        public double GetRSquared()
        {
            lock (_syncRoot)
            {
                if (_count < 2 || _sxx <= 0.0 || _syy <= 0.0)
                    return double.NaN;

                double denominator = _sxx * _syy;

                if (denominator <= 0.0)
                    return double.NaN;

                double rSquared =
                    (_sxy * _sxy) / denominator;

                return Math.Clamp(rSquared, 0.0, 1.0);
            }
        }

        /// <summary>
        /// Returns the residual standard error around the linear fit.
        /// Requires at least three samples.
        /// </summary>
        public double GetRegressionStandardError()
        {
            lock (_syncRoot)
            {
                if (_count < 3 || _sxx <= 0.0)
                    return double.NaN;

                double explainedSumSquares =
                    (_sxy * _sxy) / _sxx;

                double residualSumSquares =
                    Math.Max(0.0, _syy - explainedSumSquares);

                return Math.Sqrt(
                    residualSumSquares / (_count - 2));
            }
        }

        /// <summary>
        /// Predicts the value at a zero-based sample index.
        /// </summary>
        public double GetPredictedValue(double sampleIndex)
        {
            if (!double.IsFinite(sampleIndex))
                throw new ArgumentOutOfRangeException(
                    nameof(sampleIndex));

            lock (_syncRoot)
            {
                if (_count < 2 || _sxx <= 0.0)
                    return double.NaN;

                double slope = _sxy / _sxx;
                double intercept = _meanY - slope * _meanX;

                return intercept + slope * sampleIndex;
            }
        }

        private void UpdateMoments(
            double value,
            long previousCount)
        {
            /*
             * Online update of the first four central moments.
             *
             * This is an extension of Welford's algorithm and avoids
             * subtracting two large, nearly equal raw sums.
             */
            double n = _count;
            double delta = value - _mean;
            double deltaOverN = delta / n;
            double deltaOverNSquared =
                deltaOverN * deltaOverN;

            double term1 =
                delta * deltaOverN * previousCount;

            _mean += deltaOverN;

            _m4 +=
                term1
                * deltaOverNSquared
                * (n * n - 3.0 * n + 3.0)
                +
                6.0
                * deltaOverNSquared
                * _m2
                -
                4.0
                * deltaOverN
                * _m3;

            _m3 +=
                term1
                * deltaOverN
                * (n - 2.0)
                -
                3.0
                * deltaOverN
                * _m2;

            _m2 += term1;
        }

        private void UpdateRegression(
            double value,
            long sampleIndex)
        {
            double x = sampleIndex;
            double y = value;
            double n = _count;

            double deltaX = x - _meanX;
            double deltaY = y - _meanY;

            _meanX += deltaX / n;
            _meanY += deltaY / n;

            /*
             * Use the updated means for the second part of the online
             * covariance update.
             */
            _sxx += deltaX * (x - _meanX);
            _syy += deltaY * (y - _meanY);
            _sxy += deltaX * (y - _meanY);
        }
    }
}