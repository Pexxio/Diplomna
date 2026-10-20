using TicketSystem.Services;
using Xunit;

namespace TicketSystem.Tests;

//тества чистите правила за валидация на прикачени файлове - без база данни
public class AttachmentValidatorTests
{
    [Theory]
    [InlineData("screenshot.png")]
    [InlineData("Screenshot.PNG")]
    [InlineData("report.pdf")]
    [InlineData("data.xlsx")]
    [InlineData("notes.txt")]
    [InlineData("server.log")]
    public void IsAllowedFileName_AllowedExtensions_ReturnsTrue(string fileName)
    {
        Assert.True(AttachmentValidator.IsAllowedFileName(fileName));
    }

    [Theory]
    [InlineData("virus.exe")]
    [InlineData("script.js")]
    [InlineData("page.html")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("")]
    public void IsAllowedFileName_DisallowedExtensions_ReturnsFalse(string fileName)
    {
        Assert.False(AttachmentValidator.IsAllowedFileName(fileName));
    }

    [Fact]
    public void IsWithinSizeLimit_ZeroBytes_ReturnsFalse()
    {
        Assert.False(AttachmentValidator.IsWithinSizeLimit(0));
    }

    [Fact]
    public void IsWithinSizeLimit_ExactlyAtLimit_ReturnsTrue()
    {
        Assert.True(AttachmentValidator.IsWithinSizeLimit(AttachmentValidator.MaxFileSizeBytes));
    }

    [Fact]
    public void IsWithinSizeLimit_OneByteOverLimit_ReturnsFalse()
    {
        Assert.False(AttachmentValidator.IsWithinSizeLimit(AttachmentValidator.MaxFileSizeBytes + 1));
    }

    [Fact]
    public void IsWithinSizeLimit_TypicalScreenshotSize_ReturnsTrue()
    {
        // типичен 4K screenshot като PNG рядко надвишава 2-3MB
        var typicalScreenshotBytes = 2 * 1024 * 1024;

        Assert.True(AttachmentValidator.IsWithinSizeLimit(typicalScreenshotBytes));
    }
}
