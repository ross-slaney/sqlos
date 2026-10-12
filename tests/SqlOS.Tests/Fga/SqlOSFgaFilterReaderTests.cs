using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;
using SqlOS.Fga.Services;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaFilterReaderTests
{
    private TestInMemoryDbContext _context = null!;
    private SqlOSFgaAuthService _authService = null!;

    [TestInitialize]
    public void Setup()
    {
        _context = new TestInMemoryDbContext(new DbContextOptionsBuilder<TestInMemoryDbContext>()
            .UseInMemoryDatabase($"FilterReader_{Guid.NewGuid():N}")
            .Options);
        _context.Set<SqlOSFgaSubjectType>().AddRange(
            new SqlOSFgaSubjectType { Id = "user", Name = "User" },
            new SqlOSFgaSubjectType { Id = "group", Name = "Group" });
        _context.Set<SqlOSFgaSubject>().AddRange(
            new SqlOSFgaSubject { Id = "subj_reader", SubjectTypeId = "user", DisplayName = "Reader" },
            new SqlOSFgaSubject { Id = "subj_readers", SubjectTypeId = "group", DisplayName = "Readers" });
        _context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = "usr_reader", SubjectId = "subj_reader", IsActive = true });
        _context.Set<SqlOSFgaUserGroup>().Add(new SqlOSFgaUserGroup { Id = "grp_readers", SubjectId = "subj_readers", Name = "Readers", IsActive = true });
        _context.Set<SqlOSFgaUserGroupMembership>().Add(new SqlOSFgaUserGroupMembership { SubjectId = "subj_reader", UserGroupId = "grp_readers" });
        _context.Set<SqlOSFgaPermission>().Add(new SqlOSFgaPermission { Id = "perm_read", Key = "READ", Name = "Read" });
        _context.SaveChanges();
        _authService = new SqlOSFgaAuthService(
            _context,
            Options.Create(new SqlOSFgaOptions()),
            NullLogger<SqlOSFgaAuthService>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _context.Dispose();

    [TestMethod]
    public async Task Read_TakesTheSubjectsPermissionAndContext_FromBuildFilterAsync()
    {
        var filter = await _authService.BuildFilterAsync<PagedProduct>("subj_reader", "READ");

        var read = SqlOSFgaFilterReader.Read(filter);

        read.IsEmpty.Should().BeFalse();
        read.Context.Should().BeSameAs(_context);
        read.SubjectIds.Should().Be("[\"subj_reader\",\"subj_readers\"]");
        read.PermissionId.Should().Be("perm_read");
        read.Lambda.Should().BeSameAs(filter);
    }

    [TestMethod]
    public async Task Read_RecognizesTheEmptyFilter_ForAnUnknownSubjectOrPermission()
    {
        var unknownSubject = await _authService.BuildFilterAsync<PagedProduct>("subj_nobody", "READ");
        var unknownPermission = await _authService.BuildFilterAsync<PagedProduct>("subj_reader", "NOPE");

        SqlOSFgaFilterReader.Read(unknownSubject).IsEmpty.Should().BeTrue();
        SqlOSFgaFilterReader.Read(unknownPermission).IsEmpty.Should().BeTrue();
        SqlOSFgaFilterReader.Read((Expression<Func<PagedProduct, bool>>)(entity => false)).IsEmpty.Should().BeTrue();
    }

    [TestMethod]
    public void Read_RejectsAHandWrittenFilter()
    {
        Expression<Func<PagedProduct, bool>> byResource = entity => entity.ResourceId == "res_1";
        Expression<Func<PagedProduct, bool>> literalArguments = entity =>
            _context.IsResourceAccessible(entity.ResourceId, "[\"subj_reader\"]", "perm_read").Any();
        Expression<Func<PagedProduct, bool>> alwaysTrue = entity => true;

        foreach (var filter in new[] { byResource, literalArguments, alwaysTrue })
        {
            var act = () => SqlOSFgaFilterReader.Read(filter);
            act.Should().Throw<ArgumentException>()
                .WithMessage("The filter must come from ISqlOSFgaAuthService.BuildFilterAsync.*");
        }
    }

    [TestMethod]
    public void Read_RejectsParametersFromTwoDifferentFilters()
    {
        var first = new SqlOSFgaAuthorizationFilterParameters("[\"a\"]", "p1");
        var second = new SqlOSFgaAuthorizationFilterParameters("[\"b\"]", "p2");
        var entity = Expression.Parameter(typeof(PagedProduct), "entity");
        var call = Expression.Call(
            Expression.Constant(_context),
            typeof(TestInMemoryDbContext).GetMethod(nameof(TestInMemoryDbContext.IsResourceAccessible))!,
            Expression.Property(entity, nameof(PagedProduct.ResourceId)),
            Expression.Field(Expression.Constant(first), nameof(SqlOSFgaAuthorizationFilterParameters.SubjectIds)),
            Expression.Field(Expression.Constant(second), nameof(SqlOSFgaAuthorizationFilterParameters.PermissionId)));
        var any = Expression.Call(
            typeof(Queryable).GetMethods().First(method => method.Name == nameof(Queryable.Any) && method.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(SqlOSFgaAccessibleResource)),
            call);

        var act = () => SqlOSFgaFilterReader.Read(Expression.Lambda<Func<PagedProduct, bool>>(any, entity));

        act.Should().Throw<ArgumentException>();
    }
}
