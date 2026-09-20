param([int]$ReceiverPid)
$rule = 'FRD Secure Preview Temporary'
try {
    New-NetFirewallRule -DisplayName $rule -Direction Inbound -Action Allow -Protocol TCP -LocalPort 14707 -RemoteAddress '172.20.10.12' -Profile Any -ErrorAction Stop | Out-Null
    [IO.File]::WriteAllText('D:\1\FRD\tests\SecureDesktopReceiver\firewall-status.txt', "enabled $(Get-Date -Format o)")
    $limit = (Get-Date).AddMinutes(30)
    while ((Get-Date) -lt $limit -and (Get-Process -Id $ReceiverPid -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 3 }
}
catch { [IO.File]::WriteAllText('D:\1\FRD\tests\SecureDesktopReceiver\firewall-error.txt', $_.ToString()) }
finally {
    Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText('D:\1\FRD\tests\SecureDesktopReceiver\firewall-status.txt', "removed $(Get-Date -Format o)")
}
