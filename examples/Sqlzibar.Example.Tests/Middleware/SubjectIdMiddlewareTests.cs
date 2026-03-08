using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.Api.Middleware;

namespace Sqlzibar.Example.Tests.Middleware;

[TestClass]
public class SubjectIdMiddlewareTests
{
    [TestMethod]
    public async Task InvokeAsync_WithoutHeader_Returns401()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/chains";
        var nextCalled = false;

        var middleware = new SubjectIdMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
        nextCalled.Should().BeFalse();
    }

    [TestMethod]
    public async Task InvokeAsync_SwaggerPath_SkipsCheck()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/swagger/index.html";
        var nextCalled = false;

        var middleware = new SubjectIdMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task InvokeAsync_SqlzibarPath_SkipsCheck()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/sqlzibar/api/resources";
        var nextCalled = false;

        var middleware = new SubjectIdMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task InvokeAsync_RootPath_SkipsCheck()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        var nextCalled = false;

        var middleware = new SubjectIdMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task InvokeAsync_WithHeader_StoresInItems()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/chains";
        context.Request.Headers["X-Subject-Id"] = "test_subject";
        var nextCalled = false;

        var middleware = new SubjectIdMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Items["SubjectId"].Should().Be("test_subject");
    }

    [TestMethod]
    public void GetSubjectId_WithStoredValue_ReturnsIt()
    {
        var context = new DefaultHttpContext();
        context.Items["SubjectId"] = "my_subject";

        context.GetSubjectId().Should().Be("my_subject");
    }

    [TestMethod]
    public void GetSubjectId_WithoutStoredValue_Throws()
    {
        var context = new DefaultHttpContext();

        var act = () => context.GetSubjectId();
        act.Should().Throw<InvalidOperationException>();
    }
}
