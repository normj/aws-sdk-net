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
using Amazon.Runtime.Internal.Transform;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins DateTimeUnmarshaller's string parsing to the behavior it had before the strict
    /// ISO 8601 fast path: decimal epoch seconds first, then DateTime.Parse.
    /// </summary>
    [TestClass]
    public class DateTimeUnmarshallerIso8601Tests
    {
        private static DateTime? Reference(string text, bool treatAsNullable)
        {
            decimal seconds;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out seconds))
            {
                return AWSSDKUtils.ConvertFromUnixDecimalEpochSeconds(seconds);
            }
            if (text == null)
            {
                if (treatAsNullable) { return null; }
                return DateTime.SpecifyKind(default, DateTimeKind.Utc);
            }
            return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        private static void AssertSame(string text)
        {
            foreach (var nullable in new[] { false, true })
            {
                DateTime? expected = null, actual = null;
                Type expectedError = null, actualError = null;
                try { expected = Reference(text, nullable); } catch (Exception e) { expectedError = e.GetType(); }
                try { actual = DateTimeUnmarshaller.UnmarshallInternal(text, nullable); } catch (Exception e) { actualError = e.GetType(); }

                Assert.AreEqual(expectedError, actualError, "exception type for '" + text + "'");
                Assert.AreEqual(expected.HasValue, actual.HasValue, "HasValue for '" + text + "'");
                if (expected.HasValue)
                {
                    Assert.AreEqual(expected.Value.Ticks, actual.Value.Ticks, "ticks for '" + text + "'");
                    Assert.AreEqual(expected.Value.Kind, actual.Value.Kind, "kind for '" + text + "'");
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow("2009-10-12T17:50:30.000Z")]
        [DataRow("2009-10-12T17:50:30Z")]
        [DataRow("2009-10-12T17:50:30.1Z")]
        [DataRow("2009-10-12T17:50:30.12Z")]
        [DataRow("2009-10-12T17:50:30.123Z")]
        [DataRow("2009-10-12T17:50:30.1234Z")]
        [DataRow("2009-10-12T17:50:30.12345Z")]
        [DataRow("2009-10-12T17:50:30.123456Z")]
        [DataRow("2009-10-12T17:50:30.1234567Z")]
        [DataRow("2009-10-12T17:50:30.9999999Z")]
        [DataRow("2018-08-01T00:00:00.0000000Z")]
        [DataRow("0001-01-01T00:00:00Z")]
        [DataRow("0001-01-01T00:00:00.0000000Z")]
        [DataRow("9999-12-31T23:59:59.9999999Z")]
        [DataRow("2024-02-29T12:00:00Z")]
        [DataRow("2000-02-29T12:00:00Z")]
        [DataRow("1970-01-01T00:00:00.000Z")]
        // Shapes the fast path must leave to the general parser.
        [DataRow("2009-10-12T17:50:30.12345678Z")]
        [DataRow("2009-10-12T17:50:30.Z")]
        [DataRow("2009-10-12T17:50:30,123Z")]
        [DataRow("2009-10-12T17:50:30.123z")]
        [DataRow("2009-10-12t17:50:30.123Z")]
        [DataRow("2009-10-12 17:50:30.123Z")]
        [DataRow("2009-10-12T17:50:30.123")]
        [DataRow("2009-10-12T17:50:30")]
        [DataRow("2009-10-12T17:50:30+00:00")]
        [DataRow("2009-10-12T17:50:30.123+02:00")]
        [DataRow("2009-10-12T17:50:30.123-07:00")]
        [DataRow(" 2009-10-12T17:50:30.123Z")]
        [DataRow("2009-10-12T17:50:30.123Z ")]
        [DataRow("2009-10-12")]
        [DataRow("2009-1-12T17:50:30Z")]
        [DataRow("Wed, 01 Aug 2018 00:00:00 GMT")]
        [DataRow("1533081600")]
        [DataRow("1533081600.123")]
        [DataRow("1533081600.1239")]
        [DataRow("-1")]
        [DataRow("")]
        [DataRow(null)]
        // Out-of-range fields: the general parser decides (throws).
        [DataRow("2023-02-29T12:00:00Z")]
        [DataRow("1900-02-29T12:00:00Z")]
        [DataRow("2009-13-12T17:50:30Z")]
        [DataRow("2009-00-12T17:50:30Z")]
        [DataRow("2009-10-00T17:50:30Z")]
        [DataRow("2009-10-32T17:50:30Z")]
        [DataRow("2009-04-31T17:50:30Z")]
        [DataRow("2009-10-12T24:00:00Z")]
        [DataRow("2009-10-12T23:60:00Z")]
        [DataRow("2009-10-12T23:59:60Z")]
        [DataRow("0000-01-01T00:00:00Z")]
        [DataRow("20a9-10-12T17:50:30Z")]
        [DataRow("2009-10-12T17:50:3aZ")]
        [DataRow("2009-10-12T17:50:30.12a4Z")]
        [DataRow("2009-10-12T17:50:30.-123Z")]
        [DataRow("+009-10-12T17:50:30Z")]
        [DataRow("2009-10-12T17:50:30.123ZZ")]
        [DataRow("\u0661\u0669\u0667\u0660-01-01T00:00:00Z")]
        public void MatchesGeneralParser(string text)
        {
            AssertSame(text);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void MatchesGeneralParserForRandomTimestamps()
        {
            var random = new Random(12345);
            for (int i = 0; i < 20000; i++)
            {
                var ticks = (long)(random.NextDouble() * DateTime.MaxValue.Ticks);
                var date = new DateTime(ticks, DateTimeKind.Utc);
                int digits = random.Next(0, 8);
                string format = digits == 0
                    ? "yyyy-MM-dd\\THH:mm:ss\\Z"
                    : "yyyy-MM-dd\\THH:mm:ss." + new string('f', digits) + "\\Z";
                AssertSame(date.ToString(format, CultureInfo.InvariantCulture));
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void MatchesGeneralParserForMutatedTimestamps()
        {
            var random = new Random(54321);
            const string alphabet = "0123456789-:.TZtz+ ,";
            for (int i = 0; i < 20000; i++)
            {
                var date = new DateTime((long)(random.NextDouble() * DateTime.MaxValue.Ticks), DateTimeKind.Utc);
                var chars = date.ToString("yyyy-MM-dd\\THH:mm:ss.fff\\Z", CultureInfo.InvariantCulture).ToCharArray();
                chars[random.Next(chars.Length)] = alphabet[random.Next(alphabet.Length)];
                AssertSame(new string(chars));
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void XmlContextUsesSameValues()
        {
            var values = new List<string> { "2009-10-12T17:50:30.000Z", "2009-10-12T17:50:30.1234567Z", "2009-10-12T17:50:30+02:00", "1533081600.5" };
            var xml = new StringBuilder("<R>");
            foreach (var v in values) xml.Append("<D>").Append(v).Append("</D>");
            xml.Append("</R>");

            var actual = new List<DateTime>();
            using (var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString())), false, null))
            {
                while (context.Read())
                {
                    if (context.IsStartElement && context.TestExpression("D", 2))
                        actual.Add(DateTimeUnmarshaller.Instance.Unmarshall(context));
                }
            }

            Assert.AreEqual(values.Count, actual.Count);
            for (int i = 0; i < values.Count; i++)
            {
                var expected = Reference(values[i], false).Value;
                Assert.AreEqual(expected.Ticks, actual[i].Ticks, values[i]);
                Assert.AreEqual(expected.Kind, actual[i].Kind, values[i]);
            }
        }
    }
}
