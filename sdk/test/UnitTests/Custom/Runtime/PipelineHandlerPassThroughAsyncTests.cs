using Amazon.Runtime;
using Amazon.Runtime.Credentials.Internal;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the async behavior of the handlers that return the inner handler's task directly when they
    /// have nothing to do after it (CallbackHandler without OnPostInvoke, Signer and
    /// BaseAuthResolverHandler when their pre-invoke step completes synchronously). Every failure must
    /// still surface through the returned task, never as a synchronous throw, and an
    /// OperationCanceledException must produce a canceled task, as it does from an async method.
    /// </summary>
    [TestClass]
    public class PipelineHandlerPassThroughAsyncTests : RuntimePipelineTestBase
    {
        private sealed class TestResponse : AmazonWebServiceResponse { }

        private enum InnerBehavior { Completed, Yield, ThrowSynchronously, Faulted }

        private sealed class InnerHandler : PipelineHandler
        {
            public InnerBehavior Behavior { get; set; }
            public Exception Exception { get; set; }
            public List<string> Log { get; set; }
            public TestResponse Response { get; } = new TestResponse();
            public int CallCount { get; private set; }

            public override void InvokeSync(IExecutionContext executionContext)
            {
                CallCount++;
                Log?.Add("inner");
                if (Behavior == InnerBehavior.ThrowSynchronously || Behavior == InnerBehavior.Faulted)
                    throw Exception;
            }

            public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
            {
                CallCount++;
                Log?.Add("inner");
                switch (Behavior)
                {
                    case InnerBehavior.Completed:
                        return Task.FromResult((T)(object)Response);
                    case InnerBehavior.Yield:
                        return YieldThenReturn<T>();
                    case InnerBehavior.ThrowSynchronously:
                        throw Exception;
                    default:
                        var tcs = new TaskCompletionSource<T>();
                        tcs.SetException(Exception);
                        return tcs.Task;
                }
            }

            private async Task<T> YieldThenReturn<T>()
            {
                await Task.Yield();
                Log?.Add("inner-completed");
                return (T)(object)Response;
            }
        }

        private sealed class YieldingCredentials : AWSCredentials
        {
            private readonly ImmutableCredentials _credentials = new ImmutableCredentials("AKIDEXAMPLE", "secret", null);
            public Exception Exception { get; set; }

            public override ImmutableCredentials GetCredentials() => _credentials;

            public override async Task<ImmutableCredentials> GetCredentialsAsync()
            {
                await Task.Yield();
                if (Exception != null)
                    throw Exception;
                return _credentials;
            }
        }

        private sealed class TestAuthResolverHandler : BaseAuthResolverHandler
        {
            public List<IAuthSchemeOption> Options { get; set; } = AuthSchemeOption.DEFAULT_SIGV4;

            protected override List<IAuthSchemeOption> ResolveAuthOptions(IExecutionContext executionContext) => Options;
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return e;
            }
            return null;
        }

        // ---- CallbackHandler ----

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_NoPostInvoke_RunsPreInvokeThenReturnsInnerResult()
        {
            var log = new List<string>();
            var inner = new InnerHandler { Behavior = InnerBehavior.Yield, Log = log };
            var handler = new CallbackHandler { InnerHandler = inner, OnPreInvoke = _ => log.Add("pre") };

            var result = await handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.AreSame(inner.Response, result);
            CollectionAssert.AreEqual(new[] { "pre", "inner", "inner-completed" }, log);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_NoPostInvoke_PreInvokeThrows_ReturnsFaultedTask()
        {
            var expected = new InvalidOperationException("pre");
            var inner = new InnerHandler();
            var handler = new CallbackHandler { InnerHandler = inner, OnPreInvoke = _ => throw expected };

            var task = handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.IsTrue(task.IsFaulted);
            Assert.AreSame(expected, await CaptureAsync(task));
            Assert.AreEqual(0, inner.CallCount);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_NoPostInvoke_InnerThrowsSynchronously_ReturnsFaultedTask()
        {
            var expected = new AmazonClientException("inner");
            var inner = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected };
            var handler = new CallbackHandler { InnerHandler = inner };

            var task = handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.IsTrue(task.IsFaulted);
            Assert.AreSame(expected, task.Exception.InnerException);
            Assert.AreSame(expected, await CaptureAsync(task));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_NoPostInvoke_InnerThrowsOperationCanceled_ReturnsCanceledTask()
        {
            var expected = new OperationCanceledException("canceled");
            var inner = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected };
            var handler = new CallbackHandler { InnerHandler = inner };

            var task = handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.IsTrue(task.IsCanceled);
            Assert.AreSame(expected, await CaptureAsync(task));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_NoPostInvoke_InnerFaulted_ReturnsSameFault()
        {
            var expected = new AmazonClientException("inner");
            var inner = new InnerHandler { Behavior = InnerBehavior.Faulted, Exception = expected };
            var handler = new CallbackHandler { InnerHandler = inner };

            Assert.AreSame(expected, await CaptureAsync(handler.InvokeAsync<TestResponse>(CreateTestContext())));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_NoInnerHandler_ReturnsFaultedTask()
        {
            var handler = new CallbackHandler();

            var task = handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOfType(await CaptureAsync(task), typeof(InvalidOperationException));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_WithPostInvoke_RunsPostInvokeAfterInnerCompletes()
        {
            var log = new List<string>();
            var inner = new InnerHandler { Behavior = InnerBehavior.Yield, Log = log };
            var handler = new CallbackHandler
            {
                InnerHandler = inner,
                OnPreInvoke = _ => log.Add("pre"),
                OnPostInvoke = _ => log.Add("post"),
            };

            var result = await handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.AreSame(inner.Response, result);
            CollectionAssert.AreEqual(new[] { "pre", "inner", "inner-completed", "post" }, log);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task CallbackHandler_WithPostInvoke_InnerFails_SkipsPostInvoke()
        {
            var expected = new AmazonClientException("inner");
            var postCalls = 0;
            var inner = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected };
            var handler = new CallbackHandler { InnerHandler = inner, OnPostInvoke = _ => postCalls++ };

            var task = handler.InvokeAsync<TestResponse>(CreateTestContext());

            Assert.IsTrue(task.IsFaulted);
            Assert.AreSame(expected, await CaptureAsync(task));
            Assert.AreEqual(0, postCalls);
        }

        // ---- Signer ----

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task Signer_SynchronousCredentials_SignsThenReturnsInnerResult()
        {
            var signer = new MockSigner();
            var context = CreateTestContext(signer);
            context.RequestContext.Identity = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            var inner = new InnerHandler { Behavior = InnerBehavior.Yield };
            var handler = new Signer { InnerHandler = inner };

            var result = await handler.InvokeAsync<TestResponse>(context);

            Assert.AreSame(inner.Response, result);
            Assert.AreEqual(1, signer.SignCount);
            Assert.IsTrue(context.RequestContext.IsSigned);
            Assert.AreEqual(1, inner.CallCount);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task Signer_AsynchronousCredentials_SignsBeforeCallingInner()
        {
            var signer = new MockSigner();
            var context = CreateTestContext(signer);
            context.RequestContext.Identity = new YieldingCredentials();
            var signCountSeenByInner = -1;
            var inner = new InnerHandler { Behavior = InnerBehavior.Completed };
            var handler = new Signer { InnerHandler = new MockActionHandlerAdapter(inner, () => signCountSeenByInner = signer.SignCount) };

            var result = await handler.InvokeAsync<TestResponse>(context);

            Assert.AreSame(inner.Response, result);
            Assert.AreEqual(1, signCountSeenByInner);
            Assert.IsTrue(context.RequestContext.IsSigned);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task Signer_AsynchronousCredentialsFail_ReturnsFaultedTaskWithoutCallingInner()
        {
            var expected = new AmazonClientException("credentials");
            var context = CreateTestContext(new MockSigner());
            context.RequestContext.Identity = new YieldingCredentials { Exception = expected };
            var inner = new InnerHandler();
            var handler = new Signer { InnerHandler = inner };

            Assert.AreSame(expected, await CaptureAsync(handler.InvokeAsync<TestResponse>(context)));
            Assert.AreEqual(0, inner.CallCount);
            Assert.IsFalse(context.RequestContext.IsSigned);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task Signer_InnerThrowsSynchronously_ReturnsFaultedTask()
        {
            var expected = new AmazonClientException("inner");
            var context = CreateTestContext(new MockSigner());
            context.RequestContext.Identity = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            var handler = new Signer { InnerHandler = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected } };

            var task = handler.InvokeAsync<TestResponse>(context);

            Assert.IsTrue(task.IsFaulted);
            Assert.AreSame(expected, await CaptureAsync(task));
            Assert.IsTrue(context.RequestContext.IsSigned);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task Signer_InnerThrowsOperationCanceled_ReturnsCanceledTask()
        {
            var expected = new OperationCanceledException("canceled");
            var context = CreateTestContext(new MockSigner());
            context.RequestContext.Identity = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            var handler = new Signer { InnerHandler = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected } };

            var task = handler.InvokeAsync<TestResponse>(context);

            Assert.IsTrue(task.IsCanceled);
            Assert.AreSame(expected, await CaptureAsync(task));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task Signer_AlreadySigned_DoesNotResignAndReturnsInnerResult()
        {
            var signer = new MockSigner();
            var context = CreateTestContext(signer);
            context.RequestContext.Identity = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            context.RequestContext.IsSigned = true;
            Assert.IsFalse(context.RequestContext.ClientConfig.ResignRetries);
            var inner = new InnerHandler { Behavior = InnerBehavior.Completed };
            var handler = new Signer { InnerHandler = inner };

            var result = await handler.InvokeAsync<TestResponse>(context);

            Assert.AreSame(inner.Response, result);
            Assert.AreEqual(0, signer.SignCount);
        }

        // ---- BaseAuthResolverHandler ----

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task AuthResolver_ExplicitCredentials_ResolvesIdentityThenReturnsInnerResult()
        {
            var credentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            var context = CreateTestContext();
            ((RequestContext)context.RequestContext).ExplicitAWSCredentials = credentials;
            var inner = new InnerHandler { Behavior = InnerBehavior.Yield };
            var handler = new TestAuthResolverHandler { InnerHandler = inner };

            var result = await handler.InvokeAsync<TestResponse>(context);

            Assert.AreSame(inner.Response, result);
            Assert.AreSame(credentials, context.RequestContext.Identity);
            Assert.IsInstanceOfType(context.RequestContext.Signer, typeof(AWS4Signer));
            Assert.AreEqual(1, inner.CallCount);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task AuthResolver_NoAuthOptions_ReturnsFaultedTaskWithoutCallingInner()
        {
            var context = CreateTestContext();
            var inner = new InnerHandler();
            var handler = new TestAuthResolverHandler { InnerHandler = inner, Options = new List<IAuthSchemeOption>() };

            var task = handler.InvokeAsync<TestResponse>(context);

            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOfType(await CaptureAsync(task), typeof(AmazonClientException));
            Assert.AreEqual(0, inner.CallCount);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task AuthResolver_InnerThrowsSynchronously_ReturnsFaultedTask()
        {
            var expected = new AmazonClientException("inner");
            var context = CreateTestContext();
            ((RequestContext)context.RequestContext).ExplicitAWSCredentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            var handler = new TestAuthResolverHandler { InnerHandler = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected } };

            var task = handler.InvokeAsync<TestResponse>(context);

            Assert.IsTrue(task.IsFaulted);
            Assert.AreSame(expected, await CaptureAsync(task));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task AuthResolver_InnerThrowsOperationCanceled_ReturnsCanceledTask()
        {
            var expected = new OperationCanceledException("canceled");
            var context = CreateTestContext();
            ((RequestContext)context.RequestContext).ExplicitAWSCredentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
            var handler = new TestAuthResolverHandler { InnerHandler = new InnerHandler { Behavior = InnerBehavior.ThrowSynchronously, Exception = expected } };

            var task = handler.InvokeAsync<TestResponse>(context);

            Assert.IsTrue(task.IsCanceled);
            Assert.AreSame(expected, await CaptureAsync(task));
        }

        // Runs a callback, then forwards to the wrapped handler.
        private sealed class MockActionHandlerAdapter : PipelineHandler
        {
            private readonly Action _before;

            public MockActionHandlerAdapter(IPipelineHandler inner, Action before)
            {
                InnerHandler = inner;
                _before = before;
            }

            public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
            {
                _before();
                return base.InvokeAsync<T>(executionContext);
            }
        }
    }
}
