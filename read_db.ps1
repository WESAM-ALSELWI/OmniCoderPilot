Add-Type -Path "C:\Users\AMB\source\repos\MyCoder\MyCoder\bin\Debug\net10.0\Microsoft.Data.Sqlite.dll"
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=C:\Users\AMB\source\repos\MyCoder\MyCoder\mycoder.db")
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
