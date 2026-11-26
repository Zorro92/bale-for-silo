<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
  xmlns:a="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" exclude-result-prefixes="a rescap">
  <xsl:output method="xml" indent="yes" encoding="utf-8" />

  <!-- 'true' only for debug-server builds (Bale.Xbox.csproj PrepareFFmpegManifest passes BaleDebugServer). Anything
       else, or no parameter, is the production manifest. -->
  <xsl:param name="debugServer" select="'false'" />

  <xsl:template match="@*|node()">
    <xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy>
  </xsl:template>

  <xsl:template match="a:Capabilities">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
      <!-- The debug server injects gamepad input (Services/DebugServer.cs). The Store rejects this capability, so only
           debug-server packages get it; CI's production check (scripts/check-production-package.ps1) refuses it. -->
      <xsl:if test="$debugServer = 'true'">
        <rescap:Capability Name="inputInjectionBrokered" />
      </xsl:if>
    </xsl:copy>
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
