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
using System.Collections.Generic;
using System.Globalization;
using Amazon.Runtime.Internal;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins <see cref="ClockSkewCalculator.TryParseDateHeader"/> to the result of
    /// <see cref="DateTime.TryParseExact(string, string, IFormatProvider, DateTimeStyles, out DateTime)"/> with
    /// <see cref="AWSSDKUtils.GMTDateFormat"/>, which is what it called for every input before the canonical fast path.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class ClockSkewDateHeaderParseTests
    {
        private static bool Reference(string value, out DateTime result)
        {
            if (!string.IsNullOrEmpty(value) &&
                DateTime.TryParseExact(value, AWSSDKUtils.GMTDateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
            {
                return true;
            }

            result = DateTime.MinValue;
            return false;
        }

        private static void AssertSameAsReference(string value)
        {
            var expectedOk = Reference(value, out var expected);
            var actualOk = ClockSkewCalculator.TryParseDateHeader(value, out var actual);

            Assert.AreEqual(expectedOk, actualOk, value);
            Assert.AreEqual(expected, actual, value);
            Assert.AreEqual(expected.Kind, actual.Kind, value);
        }

        private static string Format(DateTime value)
            => value.ToString(AWSSDKUtils.GMTDateFormat, CultureInfo.InvariantCulture);

        private static IEnumerable<DateTime> SampleDates()
        {
            // Every day from 1999 to 2101 (leap years, the 2000 and 2100 century rules, every day of the week),
            // at a time that changes with the day so every hour, minute and second digit is exercised.
            var start = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var day = 0; day < 365 * 103; day++)
            {
                yield return start.AddDays(day).AddSeconds((day * 7919L) % 86400);
            }

            yield return DateTime.MinValue;
            yield return new DateTime(1, 12, 31, 23, 59, 59, DateTimeKind.Utc);
            yield return new DateTime(1600, 2, 29, 12, 0, 0, DateTimeKind.Utc);
            yield return new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

            var random = new Random(12345);
            for (var i = 0; i < 20000; i++)
            {
                yield return new DateTime(DateTime.MinValue.Ticks + (long)(random.NextDouble() * (DateTime.MaxValue.Ticks - DateTime.MinValue.Ticks)), DateTimeKind.Utc);
            }
        }

        [TestMethod]
        public void CanonicalDates_ParseOnTheFastPathAndMatchTryParseExact()
        {
            foreach (var date in SampleDates())
            {
                var value = Format(date);
                Assert.IsTrue(ClockSkewCalculator.TryParseCanonicalGmtDate(value, out var fast), value);
                Assert.AreEqual(DateTimeKind.Utc, fast.Kind, value);
                Assert.AreEqual(new DateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second, DateTimeKind.Utc), fast, value);
                AssertSameAsReference(value);
            }
        }

        [TestMethod]
        public void HeaderValuesFromHttpClient_ParseOnTheFastPath()
        {
            // HttpClient's validated Date header value is the parsed DateTimeOffset formatted with "r".
            var value = new DateTimeOffset(2026, 11, 21, 4, 1, 8, TimeSpan.Zero).ToString("r", CultureInfo.InvariantCulture);
            Assert.AreEqual("Sat, 21 Nov 2026 04:01:08 GMT", value);
            Assert.IsTrue(ClockSkewCalculator.TryParseCanonicalGmtDate(value, out var parsed));
            Assert.AreEqual(new DateTime(2026, 11, 21, 4, 1, 8, DateTimeKind.Utc), parsed);
        }

        [TestMethod]
        public void NonCanonicalValues_MatchTryParseExact()
        {
            var values = new List<string>
            {
                null,
                "",
                " ",
                "not-a-date",
                "Thu, 01 Jan 2026 00:05:01 GMT",
                // Day name that does not match the date.
                "Fri, 01 Jan 2026 00:05:01 GMT",
                "Xyz, 01 Jan 2026 00:05:01 GMT",
                // Case variations of the names and of GMT.
                "thu, 01 Jan 2026 00:05:01 GMT",
                "THU, 01 Jan 2026 00:05:01 GMT",
                "Thu, 01 jan 2026 00:05:01 GMT",
                "Thu, 01 JAN 2026 00:05:01 GMT",
                "Thu, 01 Jan 2026 00:05:01 gmt",
                "Thu, 01 Jan 2026 00:05:01 UTC",
                "Thu, 01 Jan 2026 00:05:01 GMX",
                // Out-of-range fields.
                "Sun, 29 Feb 2026 00:00:00 GMT",
                "Sat, 29 Feb 2025 00:00:00 GMT",
                "Thu, 29 Feb 2024 00:00:00 GMT",
                "Mon, 29 Feb 2100 00:00:00 GMT",
                "Tue, 29 Feb 2000 00:00:00 GMT",
                "Thu, 00 Jan 2026 00:05:01 GMT",
                "Sat, 32 Jan 2026 00:05:01 GMT",
                "Wed, 31 Apr 2026 00:05:01 GMT",
                "Thu, 01 Jan 0000 00:05:01 GMT",
                "Thu, 01 Jan 2026 24:00:00 GMT",
                "Thu, 01 Jan 2026 23:60:00 GMT",
                "Thu, 01 Jan 2026 23:59:60 GMT",
                "Thu, 01 Jan 2026 99:99:99 GMT",
                // Digits and separators.
                "Thu, 1 Jan 2026 00:05:01 GMT",
                "Thu,  1 Jan 2026 00:05:01 GMT",
                "Thu, 01 Jan 26 00:05:01 GMT",
                "Thu, 01 Jan 2026 0:05:01 GMT",
                "Thu, 01 Jan 2026 00:5:01 GMT",
                "Thu, 01 Jan 2026 00:05:1 GMT",
                "Thu, 01 Jan 2026 00:05:01 GMT ",
                " Thu, 01 Jan 2026 00:05:01 GMT",
                "Thu, 01 Jan 2026 00:05:01  GMT",
                "Thu,01 Jan 2026 00:05:01 GMT",
                "Thu; 01 Jan 2026 00:05:01 GMT",
                "Thu, 01-Jan-2026 00:05:01 GMT",
                "Thu, 01 Jan 2026 00.05.01 GMT",
                "Thu, 01 Jan 2026 00:05:01GMT ",
                "Thu, 0a Jan 2026 00:05:01 GMT",
                "Thu, 01 Jan 2O26 00:05:01 GMT",
                "Thu, \u0660\u0661 Jan 2026 00:05:01 GMT",
                "Thu, \uFF10\uFF11 Jan 2026 00:05:01 GMT",
                "Thu, 01 Jan 2026 00:05:01 +0000",
                "Thursday, 01-Jan-26 00:05:01 GMT",
                "Thu Jan  1 00:05:01 2026",
                "2026-01-01T00:05:01Z",
            };

            // Every single-character substitution of a canonical value with a character from a small alphabet.
            const string canonical = "Thu, 01 Jan 2026 00:05:01 GMT";
            const string alphabet = " ,:0129AGMTJanthu-\t";
            for (var i = 0; i < canonical.Length; i++)
            {
                foreach (var c in alphabet)
                {
                    values.Add(canonical.Substring(0, i) + c + canonical.Substring(i + 1));
                }

                values.Add(canonical.Remove(i, 1));
            }

            foreach (var value in values)
            {
                AssertSameAsReference(value);
            }
        }
    }
}
