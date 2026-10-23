param([Parameter(Mandatory=$true)][string]$SvgPath,[Parameter(Mandatory=$true)][string]$PngPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
[xml]$svgDocument=Get-Content -LiteralPath $SvgPath -Raw
$visual=New-Object System.Windows.Media.DrawingVisual
$drawing=$visual.RenderOpen()
function Get-SvgBrush([string]$color) {
 if ($color -eq 'none') { return $null }
 return [System.Windows.Media.BrushConverter]::new().ConvertFromInvariantString($color)
}
function Draw-SvgNode($node,[hashtable]$parentStyle) {
 $style=$parentStyle.Clone()
 foreach($key in @('fill','stroke','stroke-width','stroke-linecap')) { if($node.HasAttribute($key)) { $style[$key]=$node.GetAttribute($key) } }
 $brush=Get-SvgBrush $style['fill']
 $pen=$null
 if($style['stroke'] -ne 'none') {
  $pen=[System.Windows.Media.Pen]::new((Get-SvgBrush $style['stroke']),[double]$style['stroke-width'])
  if($style['stroke-linecap'] -eq 'round') { $pen.StartLineCap='Round';$pen.EndLineCap='Round' }
 }
 switch($node.LocalName) {
  'circle' { $drawing.DrawEllipse($brush,$pen,[System.Windows.Point]::new([double]$node.cx,[double]$node.cy),[double]$node.r,[double]$node.r) }
  'rect' { $radius=if($node.HasAttribute('rx')){[double]$node.rx}else{0};$drawing.DrawRoundedRectangle($brush,$pen,[System.Windows.Rect]::new([double]$node.x,[double]$node.y,[double]$node.width,[double]$node.height),$radius,$radius) }
  'path' { $drawing.DrawGeometry($brush,$pen,[System.Windows.Media.Geometry]::Parse($node.d)) }
  {$_ -in 'svg','g'} { foreach($child in $node.ChildNodes) { if($child -is [System.Xml.XmlElement]) { Draw-SvgNode $child $style } } }
  default { throw "Unsupported icon SVG element: $($node.LocalName)" }
 }
}
Draw-SvgNode $svgDocument.DocumentElement @{fill='#000000';stroke='none';'stroke-width'='1';'stroke-linecap'='butt'}
$drawing.Close()
$bitmap=[System.Windows.Media.Imaging.RenderTargetBitmap]::new(1024,1024,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
$encoder=New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream=[System.IO.File]::Create($PngPath)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
