<?xml version="1.0" encoding="utf-8"?>
<!-- Bale.Xbox.csproj BaleXboxOnlyFamily: the packaging tooling writes a Windows.Universal TargetDeviceFamily into the
     generated AppxManifest.xml beside the Windows.Xbox one that Package.appxmanifest declares, which would offer the
     package to every Windows device. Keep Windows.Xbox alone; everything else is copied unchanged. -->
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
  xmlns:a="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <xsl:output method="xml" indent="yes" encoding="utf-8" />
  <xsl:template match="@*|node()">
    <xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy>
  </xsl:template>
  <xsl:template match="a:TargetDeviceFamily[@Name='Windows.Universal'][../a:TargetDeviceFamily[@Name='Windows.Xbox']]" />
</xsl:stylesheet>
