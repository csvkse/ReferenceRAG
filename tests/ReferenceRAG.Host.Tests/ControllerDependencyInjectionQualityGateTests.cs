using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 质量门禁：API 控制器构造函数与 DI 容器解析契约门禁
/// 防止再次出现“因控制器存在多构造函数且未标注特性导致 DI 激活报 500”的问题。
/// </summary>
public sealed class ControllerDependencyInjectionQualityGateTests
{
    [Fact]
    public void AllApiControllers_MustHaveSinglePublicConstructor_OrExplicitActivatorUtilitiesConstructor()
    {
        // 获取 ReferenceRAG.ApiHost 中定义的所有控制器类型
        var hostAssembly = typeof(MafChatController).Assembly;
        var controllerTypes = hostAssembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(controllerTypes);

        foreach (var type in controllerTypes)
        {
            var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.NotEmpty(publicConstructors);

            if (publicConstructors.Length > 1)
            {
                var markedConstructors = publicConstructors
                    .Where(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() != null)
                    .ToList();

                Assert.True(
                    markedConstructors.Count == 1,
                    $"控制器 [{type.FullName}] 拥有 {publicConstructors.Length} 个公共构造函数，必须有且仅有 1 个标注 [ActivatorUtilitiesConstructor] 特性以消除 DI 歧义！");
            }
        }
    }

    [Fact]
    public void MafChatController_CanBeResolvedByActivatorUtilities_WithoutAmbiguity()
    {
        var constructors = typeof(MafChatController).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.True(constructors.Length >= 1);

        if (constructors.Length > 1)
        {
            var annotated = constructors.SingleOrDefault(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() != null);
            Assert.NotNull(annotated);
        }
    }
}
