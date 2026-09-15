<?xml version="1.0" encoding="utf-8"?>
<!--
  Exemple de feuille de transformation pour une GED attendant sa propre
  structure. A livrer par client, sans recompilation du service.

  Le XML d'entree est toujours le meme : element Capture, avec Fields,
  Properties, Items et Warnings. Mettre Output:Metadata:Format a "xml" le
  temps de mettre au point la feuille, puis basculer en "xslt".
-->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">

  <xsl:output method="xml" indent="yes" encoding="UTF-8" />

  <xsl:template match="/Capture">
    <DocumentBatch>
      <Header>
        <BatchId><xsl:value-of select="CorrelationId" /></BatchId>
        <Source><xsl:value-of select="Mailbox" /></Source>
        <ImportDate><xsl:value-of select="ProcessedUtc" /></ImportDate>

        <!-- Les champs personnalises sont repris nommement : la GED attend
             des balises precises, pas une liste generique. -->
        <CompanyCode><xsl:value-of select="Fields/Field[@name='CodeSociete']" /></CompanyCode>
        <Supplier><xsl:value-of select="Fields/Field[@name='Fournisseur']" /></Supplier>
      </Header>

      <Documents>
        <!-- Seules les pieces reellement converties sont indexees : une piece
             substituee ne contient pas le document, l'indexer induirait la
             GED en erreur. -->
        <xsl:for-each select="Items/Item[Converted='true']">
          <Document>
            <File><xsl:value-of select="PdfFileName" /></File>
            <OriginalName><xsl:value-of select="FileName" /></OriginalName>
            <Pages><xsl:value-of select="PageCount" /></Pages>
            <Checksum><xsl:value-of select="Sha256" /></Checksum>
          </Document>
        </xsl:for-each>
      </Documents>

      <!-- Comptage des pieces non livrees : la GED peut declencher une
           alerte plutot que de decouvrir le manque a la relance fournisseur. -->
      <MissingCount><xsl:value-of select="count(Items/Item[Converted='false'])" /></MissingCount>
    </DocumentBatch>
  </xsl:template>
</xsl:stylesheet>
