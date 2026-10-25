// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for the Application path properties (DataPath / StreamingAssetsPath / PersistentDataPath /
/// TemporaryCachePath / ProductName / CompanyName). Verifies the Unity-style path derivation and
/// that changing ProductName/CompanyName invalidates the cached persistent/temp paths.
/// </summary>
public class ApplicationPathsTests
{
    [Fact]
    public void DataPath_DefaultsToBaseDirectory()
    {
        Assert.Equal(System.AppContext.BaseDirectory, Application.DataPath);
    }

    [Fact]
    public void StreamingAssetsPath_IsDataPathPlusContent()
    {
        var expected = Path.Combine(Application.DataPath, "Content");
        Assert.Equal(expected, Application.StreamingAssetsPath);
    }

    [Fact]
    public void ProductName_DefaultsToXEngine()
    {
        Assert.Equal("XEngine", Application.ProductName);
    }

    [Fact]
    public void RuntimePlatform_ReportsKnownHostPlatform()
    {
        Assert.NotEqual(ApplicationRuntimePlatform.Unknown, Application.RuntimePlatform);
    }

    [Fact]
    public void RuntimePlatform_ReportsIosOnlyWhenRunningOnIos()
    {
        if (OperatingSystem.IsIOS())
            Assert.Equal(ApplicationRuntimePlatform.iOS, Application.RuntimePlatform);
        else
            Assert.NotEqual(ApplicationRuntimePlatform.iOS, Application.RuntimePlatform);
    }

    [Fact]
    public void ProductNameAndCompanyName_AreSettable()
    {
        var prevProduct = Application.ProductName;
        var prevCompany = Application.CompanyName;
        try
        {
            Application.ProductName = "TestGame";
            Application.CompanyName = "TestCo";
            Assert.Equal("TestGame", Application.ProductName);
            Assert.Equal("TestCo", Application.CompanyName);
        }
        finally
        {
            Application.ProductName = prevProduct;
            Application.CompanyName = prevCompany;
        }
    }

    [Fact]
    public void PersistentDataPath_IsCreated_AndContainsProductName()
    {
        var prevProduct = Application.ProductName;
        try
        {
            Application.ProductName = "PathTestGame_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = Application.PersistentDataPath;
            Assert.True(Directory.Exists(path), "PersistentDataPath directory should be created on access");
            Assert.Contains(Application.ProductName, path);
        }
        finally
        {
            Application.ProductName = prevProduct;
        }
    }

    [Fact]
    public void TemporaryCachePath_IsCreated_AndContainsProductName()
    {
        var prevProduct = Application.ProductName;
        try
        {
            Application.ProductName = "CacheTestGame_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = Application.TemporaryCachePath;
            Assert.True(Directory.Exists(path), "TemporaryCachePath directory should be created on access");
            Assert.Contains(Application.ProductName, path);
        }
        finally
        {
            Application.ProductName = prevProduct;
        }
    }

    [Fact]
    public void ChangingProductName_InvalidatesCachedPersistentPath()
    {
        var prevProduct = Application.ProductName;
        try
        {
            Application.ProductName = "FirstGame";
            string first = Application.PersistentDataPath;

            Application.ProductName = "SecondGame";
            string second = Application.PersistentDataPath;

            Assert.NotEqual(first, second);
            Assert.Contains("SecondGame", second);
        }
        finally
        {
            Application.ProductName = prevProduct;
        }
    }

    [Fact]
    public void PersistentDataPath_DiffersFromTemporaryCachePath()
    {
        Assert.NotEqual(Application.PersistentDataPath, Application.TemporaryCachePath);
    }
}
