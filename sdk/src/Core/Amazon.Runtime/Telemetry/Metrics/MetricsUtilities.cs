/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using Amazon.Runtime.Telemetry.Metrics.NoOp;
using Amazon.Util;

namespace Amazon.Runtime.Telemetry.Metrics
{
    /// <summary>
    /// Utility class for metrics related operations.
    /// </summary>
    internal static class MetricsUtilities
    {
        /// <summary>
        /// Returns true when the configured meter provider is the SDK's no-op provider,
        /// in which case recording metrics has no observable effect.
        /// </summary>
        internal static bool IsNoOp(IClientConfig config)
        {
            return config.TelemetryProvider.MeterProvider is NoOpMeterProvider;
        }

        /// <summary>
        /// Records an error using a MonotonicCounter.
        /// </summary>
        /// <param name="requestContext">The request context object.</param>
        /// <param name="exception">The exception that occurred.</param>
        public static void RecordError(IRequestContext requestContext, Exception exception)
        {
            if (IsNoOp(requestContext.ClientConfig))
                return;

            var attributes = new Attributes();
            attributes.Set(TelemetryConstants.ExceptionTypeAttributeKey, exception.GetType().Name);

            AddMonotonicCounterValue(requestContext, TelemetryConstants.CallErrorsMetricName, TelemetryConstants.ErrorUnitName, 1, attributes);
        }

        /// <summary>
        /// Records a value using a MonotonicCounter.
        /// </summary>
        /// <param name="requestContext">The request context object.</param>
        /// <param name="metricName">The name of the metric to record.</param>
        /// <param name="unit">The unit of the metric.</param>
        /// <param name="value">The value to be recorded by the MonotonicCounter.</param>
        /// <param name="initialAttributes">The attributes associated with the metric.</param>
        public static void AddMonotonicCounterValue(IRequestContext requestContext, string metricName, string unit, long value = 1, Attributes initialAttributes = null)
        {
            if (IsNoOp(requestContext.ClientConfig))
                return;

            var serviceId = requestContext.ClientConfig.ServiceId;

            if (initialAttributes == null)
                initialAttributes = new Attributes();

            var operationName = AWSSDKUtils.ExtractOperationName(requestContext.RequestName);
            initialAttributes.Set(TelemetryConstants.MethodAttributeKey, operationName);

            initialAttributes.Set(TelemetryConstants.SystemAttributeKey, TelemetryConstants.SystemAttributeValue);
            initialAttributes.Set(TelemetryConstants.ServiceAttributeKey, serviceId);

            var scope = $"{TelemetryConstants.TelemetryScopePrefix}.{serviceId}";

            var meter = requestContext.ClientConfig.TelemetryProvider.MeterProvider.GetMeter(scope);
            var monotonicCounter = meter.CreateMonotonicCounter<long>(metricName, unit);

            monotonicCounter.Add(value, initialAttributes);
        }

        /// <summary>
        /// Measures the duration of an event in seconds. The duration is recorded into a histogram.
        /// </summary>
        /// <param name="requestContext">The request context object.</param>
        /// <param name="metricName">The name of the metric to record.</param>
        /// <param name="initialAttributes">The attributes associated with the metric.</param>
        /// <returns>A disposable object that records the duration when disposed, or null when no meter provider is registered.</returns>
        public static IDisposable MeasureDuration(IRequestContext requestContext, string metricName, Attributes initialAttributes = null)
        {
            var measurement = StartDuration(requestContext, metricName, initialAttributes);
            return measurement.IsActive ? new DurationMetricsMeasurer(measurement) : null;
        }

        /// <summary>
        /// Measures the duration of an event in seconds. The duration is recorded into a histogram.
        /// </summary>
        /// <param name="config">The client configuration object.</param>
        /// <param name="metricName">The name of the metric to record.</param>
        /// <param name="attributes">The attributes associated with the metric.</param>
        /// <returns>A disposable object that records the duration when disposed, or null when no meter provider is registered.</returns>
        public static IDisposable MeasureDuration(IClientConfig config, string metricName, Attributes attributes = null)
        {
            var measurement = StartDuration(config, metricName, attributes);
            return measurement.IsActive ? new DurationMetricsMeasurer(measurement) : null;
        }

        /// <summary>
        /// Same as <see cref="MeasureDuration(IRequestContext, string, Attributes)"/>, but returns the measurement as a
        /// struct, so a <c>using</c> statement on the result allocates nothing for it. With the no-op meter provider
        /// the result is an inactive measurement whose Dispose does nothing.
        /// </summary>
        internal static DurationMeasurement StartDuration(IRequestContext requestContext, string metricName, Attributes initialAttributes = null)
        {
            if (IsNoOp(requestContext.ClientConfig))
                return default;

            if (initialAttributes == null)
                initialAttributes = new Attributes();

            var operationName = AWSSDKUtils.ExtractOperationName(requestContext.RequestName);
            initialAttributes.Set(TelemetryConstants.MethodAttributeKey, operationName);

            return StartDuration(requestContext.ClientConfig, metricName, initialAttributes);
        }

        /// <summary>
        /// Same as <see cref="MeasureDuration(IClientConfig, string, Attributes)"/>, but returns the measurement as a
        /// struct. With the no-op meter provider the result is an inactive measurement whose Dispose does nothing.
        /// </summary>
        internal static DurationMeasurement StartDuration(IClientConfig config, string metricName, Attributes attributes = null)
        {
            if (IsNoOp(config))
                return default;

            var serviceId = config.ServiceId;

            if (attributes == null)
                attributes = new Attributes();
            attributes.Set(TelemetryConstants.ServiceAttributeKey, serviceId);
            attributes.Set(TelemetryConstants.SystemAttributeKey, TelemetryConstants.SystemAttributeValue);

            var scope = $"{TelemetryConstants.TelemetryScopePrefix}.{serviceId}";
            var meter = config.TelemetryProvider.MeterProvider.GetMeter(scope);

            return new DurationMeasurement(meter, metricName, attributes);
        }

        /// <summary>
        /// A started duration measurement. Disposing it records the elapsed time, in seconds, into the histogram.
        /// The default value is inactive and records nothing. Dispose each started measurement once, as a
        /// <c>using</c> statement does.
        /// </summary>
        internal readonly struct DurationMeasurement : IDisposable
        {
            private readonly Histogram<double> _histogram;
            private readonly Attributes _attributes;
            private readonly DateTime _startTime;
            private readonly bool _isActive;

            public DurationMeasurement(Meter meter, string metricName, Attributes attributes)
            {
                _histogram = meter.CreateHistogram<double>(metricName, TelemetryConstants.SecondsUnitName);
                _attributes = attributes;
                _startTime = AWSSDKUtils.CorrectedUtcNow;
                _isActive = true;
            }

            /// <summary>
            /// True when this measurement was started with a meter provider other than the no-op provider.
            /// </summary>
            public bool IsActive => _isActive;

            public void Dispose()
            {
                if (!_isActive)
                    return;

                // Record the duration in seconds using ticks precision to ensure high precision
                // and to avoid data loss that may occur due to rounding when using seconds.

                var ticks = (AWSSDKUtils.CorrectedUtcNow - _startTime).Ticks;
                var durationInSeconds  = (double)ticks / TimeSpan.TicksPerSecond;

                _histogram.Record(durationInSeconds, _attributes);
            }
        }

        private class DurationMetricsMeasurer: IDisposable
        {
            private readonly DurationMeasurement _measurement;

            public DurationMetricsMeasurer(DurationMeasurement measurement)
            {
                _measurement = measurement;
            }

            public void Dispose()
            {
                _measurement.Dispose();
            }
        }
    }
}
