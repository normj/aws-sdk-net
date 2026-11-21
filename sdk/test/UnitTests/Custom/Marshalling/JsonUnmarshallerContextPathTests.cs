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
using System.Linq;
using System.Text;
using System.Text.Json;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins JsonUnmarshallerContext's CurrentDepth and CurrentPath after every token to the path stack
    /// it had before (a Stack of delimiter and property-name segments over a UTF-8 path buffer), for
    /// nested, empty, escaped, non-ASCII, deep and multi-buffer documents, and pins the
    /// InvalidOperationException that a token with no open scope throws.
    /// </summary>
    [TestClass]
    public class JsonUnmarshallerContextPathTests
    {
        private static IEnumerable<string> Documents()
        {
            yield return "{}";
            yield return "[]";
            yield return "\"raw string document\"";
            yield return "{\"a\":1,\"b\":\"x\",\"c\":true,\"d\":false,\"e\":null,\"f\":1.5e3}";
            yield return "{\"Item\":{\"pk\":{\"S\":\"user#1\"},\"tags\":{\"SS\":[\"a\",\"b\"]},\"m\":{\"M\":{\"n\":{\"N\":\"1\"},\"l\":{\"L\":[{\"S\":\"x\"},{\"NULL\":true}]}}}}}";
            yield return "{\"\":{\"\":[[],[{}],{\"\":\"\"}]},\"empty\":{},\"arr\":[]}";
            yield return "{\"caf\u00e9\":{\"\u65e5\u672c\":[\"\U0001F600\"]},\"\\u0041B\":{\"x\\ny\":1,\"q\\\"\":{\"\\\\\":[1,2]}}}";
            yield return "[[[[1,2],[3]],[]],{\"a\":[{\"b\":[{\"c\":[]}]}]},\"s\",null,true]";
            yield return "{\"Responses\":{\"t\":[" + string.Join(",", Enumerable.Range(0, 200).Select(i => "{\"id\":{\"S\":\"" + new string('k', i % 40) + i + "\"},\"v\":{\"N\":\"" + i + "\"}}")) + "]},\"UnprocessedKeys\":{}}";
            yield return Nested(15);
            yield return Nested(16);
            yield return Nested(17);
            yield return Nested(40);
            yield return NestedArrays(70);
            yield return "{\"" + new string('n', 600) + "\":{\"" + new string('m', 300) + "\":[1]}}";
        }

        private static string Nested(int depth)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < depth; i++) sb.Append("{\"level").Append(i).Append("\":");
            sb.Append("\"leaf\"");
            for (int i = 0; i < depth; i++) sb.Append(",\"after").Append(i).Append("\":[").Append(i).Append("]}");
            return sb.ToString();
        }

        private static string NestedArrays(int depth)
        {
            return new string('[', depth) + "1" + new string(']', depth);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void PathAndDepthMatchOriginalStackAfterEveryToken()
        {
            int tokens = 0;
            foreach (var document in Documents())
            {
                foreach (var bufferSize in new[] { 4096, 16, 64 })
                {
                    var bytes = Encoding.UTF8.GetBytes(document);
                    using (var context = new JsonUnmarshallerContext(new MemoryStream(bytes), false, null))
                    {
                        var reader = new StreamingUtf8JsonReader(context.Stream, bufferSize);
                        var expected = new OriginalPathStack();
                        Assert.AreEqual(0, context.CurrentDepth);
                        Assert.AreEqual(string.Empty, context.CurrentPath);
                        while (context.Read(ref reader))
                        {
                            expected.Update(context, ref reader);
                            Assert.AreEqual(expected.Depth, context.CurrentDepth, $"depth after token {tokens} of {document}");
                            Assert.AreEqual(expected.Path, context.CurrentPath, $"path after token {tokens} of {document}");
                            tokens++;
                        }
                        Assert.AreEqual(expected.Depth, context.CurrentDepth);
                        Assert.AreEqual(expected.Path, context.CurrentPath);
                    }
                }
            }
            Assert.IsTrue(tokens > 5000, tokens.ToString());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void PeekAndReadKeepThePath()
        {
            // Peek reads the next token (updating the path once); the Read after it returns that token again.
            var document = "{\"L\":[{\"S\":\"a\"},{\"S\":\"b\"}],\"N\":\"1\"}";
            using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(document)), false, null))
            {
                var reader = new StreamingUtf8JsonReader(context.Stream);
                var paths = new List<string>();
                for (int i = 0; i < 36; i++)
                {
                    if (i % 3 == 0)
                    {
                        bool isEndObject = context.Peek(JsonTokenType.EndObject, ref reader);
                        bool isEndArray = context.Peek(JsonTokenType.EndArray, ref reader);
                        paths.Add("P" + (isEndObject ? "o" : isEndArray ? "a" : "") + context.CurrentDepth + ":" + context.CurrentPath);
                    }
                    else
                    {
                        bool read = context.Read(ref reader);
                        paths.Add("R" + (read ? "t" : "f") + context.CurrentDepth + ":" + context.CurrentPath + ":" + context.CurrentTokenType);
                    }
                }
                CollectionAssert.AreEqual(ExpectedPeekReadPaths, paths, string.Join(", ", paths));
            }
        }

        // Recorded from the original implementation.
        private static readonly string[] ExpectedPeekReadPaths =
        {
            "P1:/", "Rf1:/:StartObject", "Rt1:/L:PropertyName", "P2:/L/", "Rf2:/L/:StartArray", "Rt3:/L//:StartObject",
            "P3:/L//S", "Rf3:/L//S:PropertyName", "Rt3:/L//:String", "Po2:/L/", "Rf2:/L/:EndObject", "Rt3:/L//:StartObject",
            "P3:/L//S", "Rf3:/L//S:PropertyName", "Rt3:/L//:String", "Po2:/L/", "Rf2:/L/:EndObject", "Rt1:/:EndArray",
            "P1:/N", "Rf1:/N:PropertyName", "Rt1:/:String", "Po0:", "Rf0::EndObject", "Rf0::",
            "P0:", "Rf0::", "Rf0::", "P0:", "Rf0::", "Rf0::",
            "P0:", "Rf0::", "Rf0::", "P0:", "Rf0::", "Rf0::",
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ScalarWithNoOpenScopeThrowsStackEmpty()
        {
            var expectedMessage = Assert.ThrowsExactly<InvalidOperationException>(() => new Stack<int>().Peek()).Message;
            foreach (var document in new[] { "42 ", "true ", "false ", "null ", "1.5 " })
            {
                using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(document)), false, null))
                {
                    var reader = new StreamingUtf8JsonReader(context.Stream);
                    var e = ThrowsInvalidOperation(context, ref reader);
                    Assert.AreEqual(expectedMessage, e.Message, document);
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ScopeClosedWithNoOpenScopeThrowsStackEmpty()
        {
            var expectedMessage = Assert.ThrowsExactly<InvalidOperationException>(() => new Stack<int>().Pop()).Message;
            foreach (var document in new[] { "{}", "[]", "{\"a\":1}" })
            {
                using (var context = new JsonUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(document)), false, null))
                {
                    var reader = new StreamingUtf8JsonReader(context.Stream);
                    // Read the opening token past the context, so the context sees its closing token with no scope.
                    Assert.IsTrue(reader.Read());
                    if (document.Length > 2)
                    {
                        Assert.IsTrue(reader.Read());
                        Assert.IsTrue(reader.Read());
                    }
                    var e = ThrowsInvalidOperation(context, ref reader);
                    Assert.AreEqual(expectedMessage, e.Message, document);
                }
            }
        }

        private static InvalidOperationException ThrowsInvalidOperation(JsonUnmarshallerContext context, ref StreamingUtf8JsonReader reader)
        {
            try
            {
                context.Read(ref reader);
            }
            catch (Exception e)
            {
                Assert.AreEqual(typeof(InvalidOperationException), e.GetType(), e.ToString());
                return (InvalidOperationException)e;
            }
            Assert.Fail("Expected InvalidOperationException");
            return null;
        }

        // The context's path bookkeeping before it kept segments in an array: a Stack of
        // (delimiter, length) segments over a UTF-8 path, updated as UpdateContext did.
        private sealed class OriginalPathStack
        {
            private readonly Stack<(bool Delimiter, int Length)> _stack = new Stack<(bool, int)>();
            private readonly List<byte> _path = new List<byte>();

            public int Depth { get; private set; }
            public string Path => Encoding.UTF8.GetString(_path.ToArray());

            public void Update(JsonUnmarshallerContext context, ref StreamingUtf8JsonReader reader)
            {
                var token = context.CurrentTokenType.Value;
                if (token == JsonTokenType.StartObject || token == JsonTokenType.StartArray)
                {
                    Depth++;
                    _path.Add((byte)'/');
                    _stack.Push((true, 1));
                }
                else if (token == JsonTokenType.EndObject || token == JsonTokenType.EndArray)
                {
                    if (_stack.Peek().Delimiter)
                    {
                        Pop();
                        if (_stack.Count > 0 && !_stack.Peek().Delimiter)
                            Pop();
                    }
                }
                else if (token == JsonTokenType.PropertyName)
                {
                    PushName(Encoding.UTF8.GetBytes(reader.GetString()));
                }
                else if (_stack.Count == 0 && token == JsonTokenType.String)
                {
                    PushName(Encoding.UTF8.GetBytes(reader.GetString()));
                }
                else if (token != JsonTokenType.None && !_stack.Peek().Delimiter)
                {
                    Pop();
                }
            }

            private void PushName(byte[] name)
            {
                _path.AddRange(name);
                _stack.Push((false, name.Length));
            }

            private void Pop()
            {
                var segment = _stack.Pop();
                if (segment.Delimiter)
                    Depth--;
                _path.RemoveRange(_path.Count - segment.Length, segment.Length);
            }
        }
    }
}
