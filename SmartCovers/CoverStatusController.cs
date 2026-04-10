using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SmartCovers;

/// <summary>
/// API controller exposing tool availability status for the admin config page.
/// </summary>
[ApiController]
[Route("SmartCovers")]
[Authorize(Policy = "RequiresElevation")]
public class CoverStatusController : ControllerBase
{
    private readonly CoverImageProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverStatusController"/> class.
    /// </summary>
    public CoverStatusController(CoverImageProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Returns tool availability status for cover extraction.
    /// </summary>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CoverStatus> GetStatus()
    {
        return new CoverStatus
        {
            PdfRenderingAvailable = true,
            FfmpegAvailable = _provider.GetFfmpegPath() != null,
            OnlineCoverFetchEnabled = Plugin.Instance?.Configuration?.EnableOnlineCoverFetch ?? true
        };
    }
}

/// <summary>
/// Status response indicating tool availability for cover extraction.
/// </summary>
public class CoverStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether PDF rendering is available (always true — built-in via PDFium).
    /// </summary>
    public bool PdfRenderingAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ffmpeg is available.
    /// </summary>
    public bool FfmpegAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether online cover fetching is enabled.
    /// </summary>
    public bool OnlineCoverFetchEnabled { get; set; }
}
