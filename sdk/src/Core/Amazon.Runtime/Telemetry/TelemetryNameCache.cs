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
using System.Collections.Concurrent;
using Amazon.Util;

namespace Amazon.Runtime.Telemetry
{
    /// <summary>
    /// Reuses the strings the SDK passes to a telemetry provider on every request: the
    /// "AWSSDK.{serviceId}" scope, the operation name derived from the request type name, the call
    /// span name and the "host:port" server address. Without it each metric and span built these
    /// strings again, roughly ten times per request when a provider is configured.
    /// <para />
    /// Each cache holds only immutable strings that are equal to what the uncached code produced
    /// for the same inputs, and stops adding entries once it reaches its size limit; a key beyond
    /// the limit is computed on every call as before.
    /// </summary>
    internal static class TelemetryNameCache
    {
        internal const int MaxEntries = 512;

        private static readonly ConcurrentDictionary<string, string> _scopes =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, string> _operationNames =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<(string ServiceId, string RequestName), string> _callSpanNames =
            new ConcurrentDictionary<(string ServiceId, string RequestName), string>();

        private static readonly ConcurrentDictionary<(string Host, int Port), string> _serverAddresses =
            new ConcurrentDictionary<(string Host, int Port), string>();

        /// <summary>
        /// Returns <c>$"{TelemetryConstants.TelemetryScopePrefix}.{serviceId}"</c>.
        /// </summary>
        internal static string GetScope(string serviceId)
        {
            if (serviceId == null)
                return BuildScope(null);

            if (_scopes.TryGetValue(serviceId, out var scope))
                return scope;

            scope = BuildScope(serviceId);
            if (_scopes.Count < MaxEntries)
                _scopes.TryAdd(serviceId, scope);
            return scope;
        }

        /// <summary>
        /// Returns <c>AWSSDKUtils.ExtractOperationName(requestName)</c>.
        /// </summary>
        internal static string GetOperationName(string requestName)
        {
            // A null name takes the uncached path so it fails exactly as before.
            if (requestName == null)
                return AWSSDKUtils.ExtractOperationName(requestName);

            if (_operationNames.TryGetValue(requestName, out var operationName))
                return operationName;

            operationName = AWSSDKUtils.ExtractOperationName(requestName);
            if (_operationNames.Count < MaxEntries)
                _operationNames.TryAdd(requestName, operationName);
            return operationName;
        }

        /// <summary>
        /// Returns <c>$"{serviceId}.{AWSSDKUtils.ExtractOperationName(requestName)}"</c>.
        /// </summary>
        internal static string GetCallSpanName(string serviceId, string requestName)
        {
            if (requestName == null)
                return BuildCallSpanName(serviceId, AWSSDKUtils.ExtractOperationName(requestName));

            var key = (serviceId, requestName);
            if (_callSpanNames.TryGetValue(key, out var spanName))
                return spanName;

            spanName = BuildCallSpanName(serviceId, GetOperationName(requestName));
            if (_callSpanNames.Count < MaxEntries)
                _callSpanNames.TryAdd(key, spanName);
            return spanName;
        }

        /// <summary>
        /// Returns <c>$"{host}:{port.ToString()}"</c>.
        /// </summary>
        internal static string GetServerAddress(string host, int port)
        {
            // A negative port is formatted with the current culture's negative sign, so it is never cached.
            if (host == null || port < 0)
                return BuildServerAddress(host, port);

            var key = (host, port);
            if (_serverAddresses.TryGetValue(key, out var address))
                return address;

            address = BuildServerAddress(host, port);
            if (_serverAddresses.Count < MaxEntries)
                _serverAddresses.TryAdd(key, address);
            return address;
        }

        private static string BuildScope(string serviceId)
            => $"{TelemetryConstants.TelemetryScopePrefix}.{serviceId}";

        private static string BuildCallSpanName(string serviceId, string operationName)
            => $"{serviceId}.{operationName}";

        private static string BuildServerAddress(string host, int port)
            => $"{host}:{port.ToString()}";
    }
}
