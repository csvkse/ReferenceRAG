using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.ApiHost.Controllers;
using ReferenceRAG.ApiHost.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class FileSystemControllerTests
{
    [Fact]
    public void FileBrowserPath_Normalize_EmptyOrRoot_ReturnsSlash()
    {
        Assert.Equal("/", FileBrowserPath.Normalize(null));
        Assert.Equal("/", FileBrowserPath.Normalize(""));
        Assert.Equal("/", FileBrowserPath.Normalize("   "));
        Assert.Equal("/", FileBrowserPath.Normalize("/"));
    }

    [Fact]
    public void FileBrowserPath_Normalize_InvalidControlChar_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("/path/with\0null"));
        Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("/path/with\u0007bell"));
        Assert.Throws<ArgumentException>(() => FileBrowserPath.Normalize("/path/with\nnewline"));
    }

    [Fact]
    public void FileBrowserPath_Parent_And_Name_WorkCorrectly()
    {
        Assert.Null(FileBrowserPath.Parent("/"));

        if (OperatingSystem.IsWindows())
        {
            var drive = "C:/";
            Assert.True(FileBrowserPath.IsRoot(drive));
            Assert.Equal("/", FileBrowserPath.Parent(drive));

            var subDir = "C:/Windows";
            Assert.Equal(drive, FileBrowserPath.Parent(subDir));
            Assert.Equal("Windows", FileBrowserPath.Name(subDir));
        }
        else
        {
            Assert.Equal("/", FileBrowserPath.Parent("/app"));
            Assert.Equal("/app", FileBrowserPath.Parent("/app/data"));
            Assert.Equal("data", FileBrowserPath.Name("/app/data"));
        }
    }

    [Fact]
    public void FileSystemController_Browse_VirtualRoot_Returns200WithEntries()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var result = controller.Browse("/") as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);

        var response = result.Value as FileSystemBrowseResponse;
        Assert.NotNull(response);
        Assert.Equal("/", response.Path);
        Assert.True(response.IsRoot);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(response.IsVirtualRoot);
            Assert.NotEmpty(response.Entries);
        }
    }

    [Fact]
    public void FileSystemController_Browse_NonExistent_Returns404NotFound()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var fakePath = OperatingSystem.IsWindows() ? "C:/NonExistentDirectory_For_ReferenceRAG_Test_12345" : "/tmp/non_existent_referencerag_12345";
        var result = controller.Browse(fakePath) as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public void FileSystemController_Browse_CurrentAppDirectory_ReturnsEntries()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var currentDir = AppContext.BaseDirectory;
        var result = controller.Browse(currentDir, folderOnly: false) as OkObjectResult;

        Assert.NotNull(result);
        var response = result.Value as FileSystemBrowseResponse;
        Assert.NotNull(response);
        Assert.NotEmpty(response.Entries);
    }

    [Fact]
    public void FileSystemController_CreateDirectory_Success_And_Conflict()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var tempParent = Path.Combine(Path.GetTempPath(), "ReferenceRAG_Test_Dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempParent);

        try
        {
            var targetSub = Path.Combine(tempParent, "SubFolder1");

            // 1. 创建成功
            var createResult = controller.CreateDirectory(new CreateDirectoryRequest(targetSub)) as OkObjectResult;
            Assert.NotNull(createResult);
            var createResp = createResult.Value as CreateDirectoryResponse;
            Assert.NotNull(createResp);
            Assert.True(createResp.Success);
            Assert.True(Directory.Exists(targetSub));

            // 2. 再次创建同名目录 -> 409 Conflict
            var conflictResult = controller.CreateDirectory(new CreateDirectoryRequest(targetSub)) as ConflictObjectResult;
            Assert.NotNull(conflictResult);
            Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempParent))
            {
                Directory.Delete(tempParent, true);
            }
        }
    }

    [Fact]
    public void FileSystemController_Browse_WithFiles_ReturnsCorrectStatsAndEntries()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var tempParent = Path.Combine(Path.GetTempPath(), "ReferenceRAG_Browse_Stats_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempParent);
        Directory.CreateDirectory(Path.Combine(tempParent, "Sub1"));
        Directory.CreateDirectory(Path.Combine(tempParent, "Sub2"));
        File.WriteAllText(Path.Combine(tempParent, "file1.txt"), "hello");
        File.WriteAllText(Path.Combine(tempParent, "file2.md"), "world");

        try
        {
            // folderOnly = false: 返回目录和文件，并包含统计
            var resultAll = controller.Browse(tempParent, folderOnly: false) as OkObjectResult;
            Assert.NotNull(resultAll);
            var respAll = resultAll.Value as FileSystemBrowseResponse;
            Assert.NotNull(respAll);
            Assert.Equal(2, respAll.TotalDirectories);
            Assert.Equal(2, respAll.TotalFiles);
            Assert.Equal(4, respAll.Entries.Count);
            Assert.Contains(respAll.Entries, e => e.IsDirectory && e.Name == "Sub1");
            Assert.Contains(respAll.Entries, e => !e.IsDirectory && e.Name == "file1.txt");

            // folderOnly = true: Entries 仅包含目录，但统计包含文件
            var resultFolders = controller.Browse(tempParent, folderOnly: true) as OkObjectResult;
            Assert.NotNull(resultFolders);
            var respFolders = resultFolders.Value as FileSystemBrowseResponse;
            Assert.NotNull(respFolders);
            Assert.Equal(2, respFolders.TotalDirectories);
            Assert.Equal(2, respFolders.TotalFiles);
            Assert.Equal(2, respFolders.Entries.Count);
            Assert.All(respFolders.Entries, e => Assert.True(e.IsDirectory));
        }
        finally
        {
            if (Directory.Exists(tempParent))
            {
                Directory.Delete(tempParent, true);
            }
        }
    }

    [Fact]
    public async Task FileSystemController_ReadText_And_WriteText_WithSandbox_Works()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var tempParent = Path.Combine(Path.GetTempPath(), "ReferenceRAG_FS_CRUD_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempParent);

        try
        {
            var testFile = Path.Combine(tempParent, "note.md");

            // 1. Write text inside sandbox
            var writeRes = await controller.WriteText(new WriteTextRequest(testFile, "# Title\nHello world", tempParent)) as OkObjectResult;
            Assert.NotNull(writeRes);
            var writeVal = writeRes.Value as WriteTextResponse;
            Assert.NotNull(writeVal);
            Assert.True(writeVal.Success);
            Assert.True(File.Exists(testFile));

            // 2. Read text inside sandbox
            var readRes = await controller.ReadText(testFile, tempParent) as OkObjectResult;
            Assert.NotNull(readRes);
            var readVal = readRes.Value as ReadTextResponse;
            Assert.NotNull(readVal);
            Assert.Equal("# Title\nHello world", readVal.Content);

            // 3. Sandbox violation -> 403 Forbidden
            var outsideDir = Path.GetTempPath();
            var violationRes = await controller.ReadText(testFile, Path.Combine(tempParent, "SubDirOnly")) as ObjectResult;
            Assert.NotNull(violationRes);
            Assert.Equal(StatusCodes.Status403Forbidden, violationRes.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempParent)) Directory.Delete(tempParent, true);
        }
    }

    [Fact]
    public void FileSystemController_ExecuteAction_Delete_Rename_Copy_Works()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var tempParent = Path.Combine(Path.GetTempPath(), "ReferenceRAG_FS_Action_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempParent);

        try
        {
            var fileA = Path.Combine(tempParent, "a.txt");
            var fileB = Path.Combine(tempParent, "b.txt");
            var fileC = Path.Combine(tempParent, "c.txt");
            File.WriteAllText(fileA, "content A");

            // Copy
            var copyRes = controller.ExecuteAction(new FileActionRequest("copy", fileA, fileB, SandBoxRoot: tempParent)) as OkObjectResult;
            Assert.NotNull(copyRes);
            Assert.True(File.Exists(fileA));
            Assert.True(File.Exists(fileB));

            // Rename
            var renameRes = controller.ExecuteAction(new FileActionRequest("rename", fileB, fileC, SandBoxRoot: tempParent)) as OkObjectResult;
            Assert.NotNull(renameRes);
            Assert.False(File.Exists(fileB));
            Assert.True(File.Exists(fileC));

            // Delete
            var delRes = controller.ExecuteAction(new FileActionRequest("delete", fileC, SandBoxRoot: tempParent)) as OkObjectResult;
            Assert.NotNull(delRes);
            Assert.False(File.Exists(fileC));
        }
        finally
        {
            if (Directory.Exists(tempParent)) Directory.Delete(tempParent, true);
        }
    }

    [Fact]
    public void FileSystemController_Download_SingleFile_And_FolderZip_Works()
    {
        var controller = new FileSystemController(NullLogger<FileSystemController>.Instance);
        var tempParent = Path.Combine(Path.GetTempPath(), "ReferenceRAG_FS_Download_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempParent);

        try
        {
            var fileA = Path.Combine(tempParent, "readme.txt");
            File.WriteAllText(fileA, "test file download");

            // 1. Single file download -> PhysicalFileResult
            var fileDlRes = controller.Download(fileA, tempParent) as PhysicalFileResult;
            Assert.NotNull(fileDlRes);
            Assert.Equal("readme.txt", fileDlRes.FileDownloadName);

            // 2. Folder download -> FileStreamResult (zip)
            var folderDlRes = controller.Download(tempParent, tempParent) as FileStreamResult;
            Assert.NotNull(folderDlRes);
            Assert.Equal("application/zip", folderDlRes.ContentType);
            Assert.EndsWith(".zip", folderDlRes.FileDownloadName);
            folderDlRes.FileStream.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempParent)) Directory.Delete(tempParent, true);
        }
    }
}
