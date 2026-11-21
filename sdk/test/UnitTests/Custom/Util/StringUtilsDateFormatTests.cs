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
using Amazon.Runtime.Internal.Util;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the StringUtils timestamp formatters to DateTime.ToString with the AWSSDKUtils format
    /// strings, which is how they were implemented before the .NET 8+ fast path.
    /// </summary>
    [TestClass]
    public class StringUtilsDateFormatTests
    {
        private static IEnumerable<DateTime> Dates()
        {
            yield return DateTime.MinValue;
            yield return DateTime.MaxValue;
            yield return new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            yield return new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999);
            yield return new DateTime(999, 6, 7, 8, 9, 10, 11, DateTimeKind.Utc);
            yield return new DateTime(2024, 2, 29, 12, 0, 0, 0, DateTimeKind.Utc);
            yield return new DateTime(2024, 5, 17, 13, 45, 12, 345, DateTimeKind.Utc);
            yield return new DateTime(2024, 5, 17, 13, 45, 12, 5, DateTimeKind.Utc).AddTicks(9999);
            yield return new DateTime(2024, 5, 17, 13, 45, 12, 0, DateTimeKind.Utc).AddTicks(1);
            yield return new DateTime(2024, 5, 17, 13, 45, 12, 345, DateTimeKind.Local);
            yield return new DateTime(2024, 5, 17, 13, 45, 12, 345, DateTimeKind.Unspecified);

            var random = new Random(4242);
            var kinds = new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified };
            for (int i = 0; i < 20000; i++)
            {
                // Stay a day inside the range so Local/Unspecified conversions cannot overflow.
                long ticks = TimeSpan.TicksPerDay + (long)(random.NextDouble() * (DateTime.MaxValue.Ticks - 2 * TimeSpan.TicksPerDay));
                yield return new DateTime(ticks, kinds[i % 3]);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void FormattersMatchCustomFormatStrings()
        {
            foreach (var date in Dates())
            {
                var utc = date.ToUniversalTime();
                string iso = utc.ToString(AWSSDKUtils.ISO8601DateFormat, CultureInfo.InvariantCulture);
                string isoNoMs = utc.ToString(AWSSDKUtils.ISO8601DateFormatNoMS, CultureInfo.InvariantCulture);
                string rfc822 = utc.ToString(AWSSDKUtils.RFC822DateFormat, CultureInfo.InvariantCulture);
                string optional = date.Millisecond == 0 ? isoNoMs : iso;

                Assert.AreEqual(iso, StringUtils.FromDateTimeToISO8601(date));
                Assert.AreEqual(iso, StringUtils.FromDateTimeToISO8601((DateTime?)date));
                Assert.AreEqual(isoNoMs, StringUtils.FromDateTimeToISO8601NoMs(date));
                Assert.AreEqual(isoNoMs, StringUtils.FromDateTimeToISO8601NoMs((DateTime?)date));
                Assert.AreEqual(optional, StringUtils.FromDateTimeToISO8601WithOptionalMs(date));
                Assert.AreEqual(optional, StringUtils.FromDateTimeToISO8601WithOptionalMs((DateTime?)date));
                Assert.AreEqual(rfc822, StringUtils.FromDateTimeToRFC822(date));
                Assert.AreEqual(rfc822, StringUtils.FromDateTimeToRFC822((DateTime?)date));
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void FormattersIgnoreCurrentCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                foreach (var name in new[] { "ar-SA", "th-TH", "fa-IR", "de-DE", "ja-JP" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);
                    var date = new DateTime(2024, 5, 17, 13, 45, 12, 345, DateTimeKind.Utc);
                    Assert.AreEqual("2024-05-17T13:45:12.345Z", StringUtils.FromDateTimeToISO8601(date), name);
                    Assert.AreEqual("2024-05-17T13:45:12Z", StringUtils.FromDateTimeToISO8601NoMs(date), name);
                    Assert.AreEqual("Fri, 17 May 2024 13:45:12 GMT", StringUtils.FromDateTimeToRFC822(date), name);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NullableFormattersReturnNull()
        {
            Assert.IsNull(StringUtils.FromDateTimeToISO8601((DateTime?)null));
            Assert.IsNull(StringUtils.FromDateTimeToISO8601NoMs((DateTime?)null));
            Assert.IsNull(StringUtils.FromDateTimeToISO8601WithOptionalMs((DateTime?)null));
            Assert.IsNull(StringUtils.FromDateTimeToRFC822((DateTime?)null));
        }
    }
}
