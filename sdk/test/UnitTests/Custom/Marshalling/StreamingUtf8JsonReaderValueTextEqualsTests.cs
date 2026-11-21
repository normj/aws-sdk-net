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
using System.Text.Json;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins StreamingUtf8JsonReader.ValueTextEquals to the result of Utf8JsonReader.ValueTextEquals
    /// for every token and candidate text, including escaped, non-ASCII and invalid UTF-16 input.
    /// </summary>
    [TestClass]
    public class StreamingUtf8JsonReaderValueTextEqualsTests
    {
        private static readonly string[] Names =
        {
            "", "a", "A", "ab", "BackupArn", "BackupName", "backupname", "TableName", "TableId",
            "Name", "Nam", "Names", "é", "e\u0301", "café", "cafe", "caf\u00e9s", "日本", "日本語",
            "\U0001F600", "x\U0001F600", "a\"b", "a\\b", "line\nbreak", "tab\t", "\u007f", "\u0080",
            "Ä", "AB", "ABC", "ABCDEF",
        };

        private static readonly string[] Candidates =
        {
            "", "a", "A", "ab", "BackupArn", "BackupName", "backupname", "TableName", "TableId",
            "Name", "Nam", "Names", "é", "e\u0301", "café", "cafe", "cafés", "日本", "日本語",
            "\U0001F600", "x\U0001F600", "a\"b", "a\\b", "line\nbreak", "tab\t", "\u007f", "\u0080",
            "Ä", "AB", "ABC", "ABCDEF", "Ã©", "Ã¤", "\ud800", "a\udc00", "\ud83d", "aé", "ée",
            "Bckupaname", "TableNam\u0065", "TABLENAME",
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void MatchesUtf8JsonReaderForPlainAndEscapedNames()
        {
            foreach (var name in Names)
            {
                // Plain: the encoder escapes only what JSON requires (quotes, backslash, control chars).
                AssertSameAsUtf8JsonReader(JsonSerializer.Serialize(name, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                // Default encoder: non-ASCII becomes \uXXXX, so the value is escaped.
                AssertSameAsUtf8JsonReader(JsonSerializer.Serialize(name));
                // Every char escaped.
                var sb = new StringBuilder("\"");
                foreach (var c in name) sb.Append("\\u").Append(((int)c).ToString("x4"));
                AssertSameAsUtf8JsonReader(sb.Append('"').ToString());
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void MatchesUtf8JsonReaderAcrossBufferRefills()
        {
            // A small buffer makes StreamingUtf8JsonReader refill and re-read tokens that straddle the
            // buffer end, so the values are compared from a freshly positioned reader.
            var json = new StringBuilder("{");
            for (int i = 0; i < 200; i++)
            {
                if (i > 0) json.Append(',');
                json.Append('"').Append(Names[i % Names.Length].Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t")).Append(i % 7).Append("\":\"").Append(Candidates[i % Candidates.Length].Length).Append('"');
            }
            json.Append('}');
            var bytes = Encoding.UTF8.GetBytes(json.ToString());

            var expected = new List<bool>();
            var utf8 = new Utf8JsonReader(bytes);
            while (utf8.Read())
            {
                if (utf8.TokenType == JsonTokenType.PropertyName || utf8.TokenType == JsonTokenType.String)
                {
                    foreach (var c in Candidates) expected.Add(utf8.ValueTextEquals(c));
                    expected.Add(utf8.ValueTextEquals(utf8.GetString()));
                }
            }

            var actual = new List<bool>();
            using (var stream = new MemoryStream(bytes))
            {
                var reader = new StreamingUtf8JsonReader(stream, 64);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName || reader.TokenType == JsonTokenType.String)
                    {
                        foreach (var c in Candidates) actual.Add(reader.ValueTextEquals(c));
                        actual.Add(reader.ValueTextEquals(reader.GetString()));
                    }
                }
            }

            CollectionAssert.AreEqual(expected, actual);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void StringValuesAndNullText()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"k\":\"Name\",\"e\":\"\"}");
            using (var stream = new MemoryStream(bytes))
            {
                var reader = new StreamingUtf8JsonReader(stream);
                reader.Read(); // {
                reader.Read(); // "k"
                Assert.IsTrue(reader.ValueTextEquals("k"));
                Assert.IsFalse(reader.ValueTextEquals(null));
                reader.Read(); // "Name"
                Assert.AreEqual(JsonTokenType.String, reader.TokenType);
                Assert.IsTrue(reader.ValueTextEquals("Name"));
                Assert.IsFalse(reader.ValueTextEquals("name"));
                Assert.IsFalse(reader.ValueTextEquals("Names"));
                reader.Read(); // "e"
                reader.Read(); // ""
                // A null string is passed on as an empty span, as before.
                Assert.IsTrue(reader.ValueTextEquals(null));
                Assert.IsTrue(reader.ValueTextEquals(""));
                Assert.IsFalse(reader.ValueTextEquals("a"));
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NonTextTokensStillThrow()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":true,\"c\":null,\"d\":[{}]}");
            using (var stream = new MemoryStream(bytes))
            {
                var reader = new StreamingUtf8JsonReader(stream);
                int checkedTokens = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                        continue;

                    var type = reader.TokenType;
                    try
                    {
                        reader.ValueTextEquals("1");
                        Assert.Fail("Expected InvalidOperationException for " + type);
                    }
                    catch (InvalidOperationException)
                    {
                        checkedTokens++;
                    }
                }
                Assert.IsTrue(checkedTokens >= 7, checkedTokens.ToString());
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ContextTestExpressionMatchesMembers()
        {
            var json = "{\"BackupArn\":\"x\",\"Backup\\u004eame\":\"y\",\"caf\u00e9\":1,\"TableName\":{\"TableName\":2}}";
            using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(json)), false, null))
            {
                var reader = new StreamingUtf8JsonReader(context.Stream);
                context.Read(ref reader);
                var matches = new List<string>();
                while (context.ReadAtDepth(1, ref reader))
                {
                    foreach (var name in new[] { "BackupArn", "BackupName", "café", "cafe", "TableName", "Table" })
                    {
                        if (context.TestExpression(name, 1, ref reader)) matches.Add(name + "@" + context.CurrentDepth);
                        if (context.TestExpression(name, 2, ref reader)) matches.Add(name + "@" + context.CurrentDepth);
                    }
                }
                CollectionAssert.AreEqual(new[] { "BackupArn@1", "BackupName@1", "café@1", "TableName@1", "TableName@2" }, matches);
            }
        }

        private static void AssertSameAsUtf8JsonReader(string jsonString)
        {
            foreach (var json in new[] { "{" + jsonString + ":" + jsonString + "}", "[" + jsonString + "]" })
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var expected = new List<bool>();
                var utf8 = new Utf8JsonReader(bytes);
                while (utf8.Read())
                {
                    if (utf8.TokenType == JsonTokenType.PropertyName || utf8.TokenType == JsonTokenType.String)
                        foreach (var c in Candidates) expected.Add(utf8.ValueTextEquals(c));
                }

                var actual = new List<bool>();
                using (var stream = new MemoryStream(bytes))
                {
                    var reader = new StreamingUtf8JsonReader(stream);
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName || reader.TokenType == JsonTokenType.String)
                            foreach (var c in Candidates) actual.Add(reader.ValueTextEquals(c));
                    }
                }

                CollectionAssert.AreEqual(expected, actual, json);
            }
        }
    }
}
