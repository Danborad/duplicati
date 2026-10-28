// Copyright (C) 2026, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Duplicati.WebserverCore.Exceptions;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

#nullable enable

namespace Duplicati.UnitTest;

public class FilesystemEndpointTests : BasicSetupHelper
{
    private static string ExpandPath(string path)
    {
        var method = typeof(WebserverCore.Endpoints.V1.Filesystem)
            .GetMethod("ExpandPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "ExpandPath method not found via reflection");
        try
        {
            return (string)method!.Invoke(null, new object[] { path })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static IEnumerable<WebserverCore.Dto.TreeNodeDto> ListFolderAsNodes(string entrypath, bool skipFiles, bool showHidden)
    {
        var method = typeof(WebserverCore.Endpoints.V1.Filesystem)
            .GetMethod("ListFolderAsNodes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "ListFolderAsNodes method not found via reflection");
        try
        {
            return (IEnumerable<WebserverCore.Dto.TreeNodeDto>)method!.Invoke(null, new object[] { entrypath, skipFiles, showHidden })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    [Test]
    [Category("FilesystemEndpoint")]
    public void ExpandPathThrowsOnEmptyOrNull()
    {
        Assert.Throws<BadRequestException>(() => ExpandPath(""));
    }

    [Test]
    [Category("FilesystemEndpoint")]
    public void ExpandPathHandlesAbsolutePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("C:\\test\\path", ExpandPath("C:\\test\\path"));
        }
        else
        {
            Assert.AreEqual("/test/path", ExpandPath("/test/path"));
        }
    }

    [Test]
    [Category("FilesystemEndpoint")]
    public void ExpandPathExpandsTildeOnUnix()
    {
        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var expandedHome = ExpandPath("~");
            Assert.AreEqual(home, expandedHome);

            var expandedSub = ExpandPath("~/subfolder");
            Assert.AreEqual(Path.Combine(home, "subfolder"), expandedSub);
        }
    }

    [Test]
    [Category("FilesystemEndpoint")]
    public void ListFolderAsNodesReturnsEmptyOnMissingFolderWithoutCrashing()
    {
        var nonExistentPath = Path.Combine(DATAFOLDER, "definitely_does_not_exist_folder_" + Guid.NewGuid().ToString("N"));
        var nodes = ListFolderAsNodes(nonExistentPath, false, false).ToList();
        Assert.AreEqual(0, nodes.Count);
    }

    [Test]
    [Category("FilesystemEndpoint")]
    public void ListFolderAsNodesListsExistingDirectory()
    {
        var testDir = Path.Combine(DATAFOLDER, "test_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            var subDir = Path.Combine(testDir, "sub_folder");
            Directory.CreateDirectory(subDir);
            var testFile = Path.Combine(testDir, "test_file.txt");
            File.WriteAllText(testFile, "hello");

            var nodes = ListFolderAsNodes(testDir, false, true).ToList();
            Assert.AreEqual(2, nodes.Count);

            var folderNode = nodes.FirstOrDefault(n => n.cls == "folder");
            Assert.IsNotNull(folderNode, "Folder node should be present");
            Assert.AreEqual("sub_folder", folderNode!.text);

            var fileNode = nodes.FirstOrDefault(n => n.cls == "file");
            Assert.IsNotNull(fileNode, "File node should be present");
            Assert.AreEqual("test_file.txt", fileNode!.text);
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }
    }
}
