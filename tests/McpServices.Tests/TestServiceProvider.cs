using Microsoft.Extensions.DependencyInjection;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.AspNetCore.Http;

namespace McpServices.Tests;

/// <summary>
/// Custom service provider for testing that can handle GetRequiredService calls
/// </summary>
public class TestServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();
    
    public TestServiceProvider()
    {
        // Register default test services
    }
    
    public void RegisterService<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }
    
    public object? GetService(Type serviceType)
    {
        return _services.TryGetValue(serviceType, out var service) ? service : null;
    }
    
    public T GetRequiredService<T>() where T : notnull
    {
        var service = GetService(typeof(T));
        if (service == null)
        {
            throw new InvalidOperationException($"Service of type {typeof(T).Name} not registered");
        }
        return (T)service;
    }
}