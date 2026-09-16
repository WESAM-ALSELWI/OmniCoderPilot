$dllPath = Join-Path $PSScriptRoot "OmniCoderPilot.Wpf\bin\Debug\net10.0-windows\Microsoft.Data.Sqlite.dll"
if (Test-Path $dllPath) { Add-Type -Path $dllPath }
$dbPath = Join-Path $PSScriptRoot "omnicoderpilot.db"
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath")
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Role, Content, MetadataJson FROM Messages ORDER BY Id DESC LIMIT 30"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    $role = $reader.GetValue(0)
    $content = $reader.GetValue(1)
    $meta = $reader.GetValue(2)
    Write-Host "=== Role: $role ==="
    if ($content.Length -gt 400) {
        Write-Host $content.Substring(0, 400) "..."
    } else {
        Write-Host $content
    }
    Write-Host "Metadata: $meta"
    Write-Host ""
}
$conn.Close()
