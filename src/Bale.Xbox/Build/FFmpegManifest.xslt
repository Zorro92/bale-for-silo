<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
  xmlns:a="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" exclude-result-prefixes="a">
  <xsl:output method="xml" indent="yes" encoding="utf-8" />

  <xsl:template match="@*|node()">
    <xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy>
  </xsl:template>

  <xsl:template match="a:Applications">
    <xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy>
    <!-- Native WinRT activation is a package extension, not an application extension. -->
    <Extensions>
      <Extension Category="windows.activatableClass.inProcessServer">
        <InProcessServer>
          <Path>FFmpegInteropX.dll</Path>
          <!-- Activatable and static runtime classes in FFmpegInteropX.UWP.Lib 2.1.0's winmd.
               Audio/Video/General/SubtitlesConfig have no activation factory of their own. -->
          <ActivatableClass ActivatableClassId="FFmpegInteropX.AudioStreamInfo" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.ChapterInfo" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.CharacterEncoding" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.CodecChecker" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.CodecRequiredEventArgs" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.FFmpegInteropLogging" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.FFmpegMediaSource" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.FilterCommandResult" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.FormatInfo" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.FrameGrabber" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.MediaSourceConfig" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.MediaThumbnailData" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.SubtitleParser" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.SubtitleStreamInfo" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.VideoFrame" ThreadingModel="both" />
          <ActivatableClass ActivatableClassId="FFmpegInteropX.VideoStreamInfo" ThreadingModel="both" />
        </InProcessServer>
      </Extension>
    </Extensions>
  </xsl:template>
</xsl:stylesheet>
