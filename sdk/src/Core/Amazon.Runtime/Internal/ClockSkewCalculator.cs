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
using System.Globalization;
using Amazon.Util;

namespace Amazon.Runtime.Internal
{
    /// <summary>
    /// Implements the skew computation defined by the Clock Skew Correction specification.
    ///
    /// This is the pure computation portion (no state): given the raw client
    /// send/receive timestamps and the server's <c>Date</c> response header, it
    /// produces a candidate skew as the server time minus the time the response was
    /// received, and applies the specification's discard guards (maximum trusted
    /// request duration and cache <c>Age</c> header).
    ///
    /// Persistence of the resulting skew (ClientSkew / AttemptSkew) and the
    /// retry classification live elsewhere; see <see cref="CorrectClockSkew"/> and
    /// the retry policy.
    /// </summary>
    internal static class ClockSkewCalculator
    {
        /// <summary>
        /// The minimum absolute skew value considered meaningful when deciding
        /// whether an error is clock-skew related (specification: 4 minutes).
        /// </summary>
        internal static readonly TimeSpan SkewDetectionThreshold = TimeSpan.FromMinutes(4);

        /// <summary>
        /// The longest a request may take before the skew measurement from its
        /// response is discarded as unreliable (specification: 15 minutes).
        /// </summary>
        internal static readonly TimeSpan MaxTrustedRequestDuration = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Computes a candidate clock skew from a response per the Clock Skew
        /// Correction specification.
        ///
        /// <code>
        /// elapsed   = timeResponseReceived - timeRequestSent   (used only for the trust guard)
        /// candidate = serverTime - timeResponseReceived
        /// </code>
        ///
        /// The candidate is discarded (returns <c>false</c>) when:
        /// <list type="bullet">
        /// <item>the <c>Date</c> header is absent/unparseable (<paramref name="serverTimeUtc"/> is null),</item>
        /// <item><c>elapsed</c> exceeds <see cref="MaxTrustedRequestDuration"/> (15 min),</item>
        /// <item>the response carries an <c>Age</c> header (served from a cache).</item>
        /// </list>
        /// </summary>
        /// <param name="serverTimeUtc">Parsed value of the response <c>Date</c> header in UTC, or null if absent/unparseable.</param>
        /// <param name="timeRequestSentUtc">Raw (unskewed) client UTC time when the request was sent.</param>
        /// <param name="timeResponseReceivedUtc">Raw (unskewed) client UTC time when the response status line and headers were received (before the body is read).</param>
        /// <param name="hasAgeHeader">True if the response contained an <c>Age</c> header.</param>
        /// <param name="candidateSkew">The computed candidate skew when the method returns true; otherwise <see cref="TimeSpan.Zero"/>.</param>
        /// <returns>True if a trustworthy candidate skew was produced; otherwise false.</returns>
        internal static bool TryComputeCandidateSkew(
            DateTime? serverTimeUtc,
            DateTime timeRequestSentUtc,
            DateTime timeResponseReceivedUtc,
            bool hasAgeHeader,
            out TimeSpan candidateSkew)
        {
            candidateSkew = TimeSpan.Zero;

            // No Date header (or unparseable) => no candidate.
            if (!serverTimeUtc.HasValue)
                return false;

            // Response served from a cache: the Date header does not reflect the
            // service's current time, so it MUST NOT be trusted.
            if (hasAgeHeader)
                return false;

            var elapsed = timeResponseReceivedUtc - timeRequestSentUtc;

            // Response latency is counted as skew (HTTP has no server transmit timestamp),
            // so a slow request is not trusted: a false skew beyond this bound could push
            // the signing time outside the SigV4 validity window.
            if (elapsed > MaxTrustedRequestDuration)
                return false;

            // Positive when the server clock is ahead of the client clock.
            candidateSkew = serverTimeUtc.Value - timeResponseReceivedUtc;
            return true;
        }

        /// <summary>
        /// Attempts to parse the value of an HTTP <c>Date</c> header (RFC 1123 /
        /// GMT format) into a UTC <see cref="DateTime"/>.
        /// </summary>
        /// <param name="dateHeaderValue">The raw <c>Date</c> header value.</param>
        /// <param name="serverTimeUtc">The parsed UTC time when successful.</param>
        /// <returns>True if the header was present and parseable; otherwise false.</returns>
        internal static bool TryParseDateHeader(string dateHeaderValue, out DateTime serverTimeUtc)
        {
            if (TryParseCanonicalGmtDate(dateHeaderValue, out serverTimeUtc))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(dateHeaderValue) &&
                DateTime.TryParseExact(
                    dateHeaderValue,
                    AWSSDKUtils.GMTDateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out serverTimeUtc))
            {
                return true;
            }

            serverTimeUtc = DateTime.MinValue;
            return false;
        }

        private static readonly string[] CanonicalDayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        private static readonly string[] CanonicalMonthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        /// <summary>
        /// Parses a <c>Date</c> header in the exact form servers send, <c>Sun, 06 Nov 1994 08:49:37 GMT</c>,
        /// without the general <see cref="DateTime.TryParseExact(string, string, IFormatProvider, DateTimeStyles, out DateTime)"/>
        /// machinery. Accepts only that canonical spelling (two-digit day, ASCII digits, the invariant
        /// abbreviated day and month names in their usual case, a day name that matches the date) and returns
        /// false for anything else, which the caller then hands to <c>TryParseExact</c>. For every input it
        /// accepts, the result equals what <c>TryParseExact</c> with <see cref="AWSSDKUtils.GMTDateFormat"/>
        /// returns: the same UTC instant with <see cref="DateTimeKind.Utc"/>.
        /// </summary>
        internal static bool TryParseCanonicalGmtDate(string value, out DateTime serverTimeUtc)
        {
            serverTimeUtc = DateTime.MinValue;

            // "ddd, dd MMM yyyy HH:mm:ss GMT" is always 29 characters.
            if (value == null || value.Length != 29 ||
                value[3] != ',' || value[4] != ' ' || value[7] != ' ' || value[11] != ' ' || value[16] != ' ' ||
                value[19] != ':' || value[22] != ':' || value[25] != ' ' ||
                value[26] != 'G' || value[27] != 'M' || value[28] != 'T')
            {
                return false;
            }

            if (!TryParseDigits(value, 5, 2, out var day) ||
                !TryParseDigits(value, 12, 4, out var year) ||
                !TryParseDigits(value, 17, 2, out var hour) ||
                !TryParseDigits(value, 20, 2, out var minute) ||
                !TryParseDigits(value, 23, 2, out var second))
            {
                return false;
            }

            var month = 0;
            for (var i = 0; i < CanonicalMonthNames.Length; i++)
            {
                if (string.CompareOrdinal(value, 8, CanonicalMonthNames[i], 0, 3) == 0)
                {
                    month = i + 1;
                    break;
                }
            }

            if (month == 0 || year < 1 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
                hour > 23 || minute > 59 || second > 59)
            {
                return false;
            }

            var parsed = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            if (string.CompareOrdinal(value, 0, CanonicalDayNames[(int)parsed.DayOfWeek], 0, 3) != 0)
            {
                return false;
            }

            serverTimeUtc = parsed;
            return true;
        }

        private static bool TryParseDigits(string value, int start, int count, out int result)
        {
            result = 0;
            for (var i = start; i < start + count; i++)
            {
                var digit = value[i] - '0';
                if ((uint)digit > 9)
                {
                    return false;
                }

                result = result * 10 + digit;
            }

            return true;
        }
    }
}
