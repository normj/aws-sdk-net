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
using System.IO;
using System.Text;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins the JSON number path of DateTimeUnmarshaller and NullableDateTimeUnmarshaller (epoch
    /// seconds) to the decimal conversion it used before: (long)(seconds * 1000) milliseconds added
    /// to the epoch, including truncation, rounding of very long numbers, and exceptions.
    /// </summary>
    [TestClass]
    public class JsonEpochDateTimeUnmarshallerTests
    {
        private static IEnumerable<string> Numbers()
        {
            yield return "0";
            yield return "0.0";
            yield return "0.5";
            yield return "1";
            yield return "1.1";
            yield return "1.12";
            yield return "1.123";
            yield return "1.1239";
            yield return "1.9999";
            yield return "1762594202";
            yield return "1762594202.1239";
            yield return "1762594202.000";
            yield return "1762594202.999999999999999";   // 15 fraction digits
            yield return "1762594202.9999999999999999";  // 16 fraction digits: decimal path
            yield return "1.99999999999999999999999999999"; // decimal rounds this up to 2
            yield return "0.00000000000000000000000000001";
            yield return "253402300799.999";             // 9999-12-31T23:59:59.999Z
            yield return "253402300800";                 // one second past DateTime.MaxValue
            yield return "999999999999";                 // 12 integer digits, out of range
            yield return "999999999999.999";
            yield return "1000000000000";                // 13 integer digits: decimal path
            yield return "-1";
            yield return "-0.5";
            yield return "-62135596800";                 // 0001-01-01T00:00:00Z
            yield return "-62135596801";
            yield return "1.5e3";
            yield return "1E9";
            yield return "17625942020e-1";
            yield return "1e400";
            yield return "123456789012345678901234567890";
            var random = new Random(1234);
            for (int i = 0; i < 5000; i++)
            {
                var sb = new StringBuilder();
                sb.Append(random.Next(0, 2) == 0 ? random.Next(0, 10) : random.Next(1, int.MaxValue));
                int fractionDigits = random.Next(0, 18);
                if (fractionDigits > 0)
                {
                    sb.Append('.');
                    for (int d = 0; d < fractionDigits; d++) sb.Append((char)('0' + random.Next(0, 10)));
                }
                yield return sb.ToString();
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void EpochNumbersMatchDecimalConversion()
        {
            int checkedCount = 0;
            foreach (var number in Numbers())
            {
                AssertSame(Expected(number, false), () => Unmarshall(number, nullable: false), number);
                AssertSame(Expected(number, true), () => Unmarshall(number, nullable: true), number);
                checkedCount++;
            }
            Assert.IsTrue(checkedCount > 5000);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void KnownValues()
        {
            Assert.AreEqual(new DateTime(2025, 11, 8, 9, 30, 2, 123, DateTimeKind.Utc), Unmarshall("1762594202.1239", false));
            Assert.AreEqual(DateTimeKind.Utc, ((DateTime)Unmarshall("1762594202.1239", false)).Kind);
            Assert.AreEqual(new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc), Unmarshall("1.99999999999999999999999999999", true));
            Assert.AreEqual(new DateTime(1970, 1, 1, 0, 0, 1, 999, DateTimeKind.Utc), Unmarshall("1.999999999999999", true));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Unmarshall("999999999999", false));
            Assert.IsNull(Unmarshall("null", true));
        }

        // The pre-change JSON number path, step by step: Utf8JsonReader.TryGetDecimal and
        // ConvertFromUnixDecimalEpochSeconds, or the raw text through UnmarshallInternal when
        // TryGetDecimal rejects the number. Returns the value or the exception type.
        private static object Expected(string number, bool nullable)
        {
            var reader = new System.Text.Json.Utf8JsonReader(Encoding.UTF8.GetBytes("[" + number + "]"));
            reader.Read();
            reader.Read();
            try
            {
                if (reader.TryGetDecimal(out var seconds))
                    return AWSSDKUtils.ConvertFromUnixDecimalEpochSeconds(seconds);
                return DateTimeUnmarshaller.UnmarshallInternal(Encoding.UTF8.GetString(reader.ValueSpan.ToArray()), nullable);
            }
            catch (Exception e)
            {
                return e.GetType();
            }
        }

        private static void AssertSame(object expected, Func<object> actual, string number)
        {
            object result;
            try
            {
                result = actual();
            }
            catch (Exception e)
            {
                result = e.GetType();
            }

            Assert.AreEqual(expected, result, number);
            if (result is DateTime dt)
                Assert.AreEqual(DateTimeKind.Utc, dt.Kind, number);
        }

        private static object Unmarshall(string number, bool nullable)
        {
            var json = "[" + number + "]";
            using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(json)), false, null))
            {
                var reader = new StreamingUtf8JsonReader(context.Stream);
                context.Read(ref reader); // [
                if (nullable)
                    return NullableDateTimeUnmarshaller.Instance.Unmarshall(context, ref reader);
                return DateTimeUnmarshaller.Instance.Unmarshall(context, ref reader);
            }
        }
    }
}
