param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][ValidateSet('install','start','stop','status','uninstall','router-add','router-remove')][string]$Action
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security
$data = Join-Path $Root '.local'
$resultPath = Join-Path $data 'admin-result.json'
$journalPath = Join-Path $data 'network-journal.json'
$wg = Join-Path $env:ProgramFiles 'WireGuard\wireguard.exe'
$wgTool = Join-Path $env:ProgramFiles 'WireGuard\wg.exe'
$script:journal = $null
$handshake = $null

function Save-Json([string]$Path, $Value) {
    $text = $Value | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($Path + '.new', $text, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($Path + '.new', $Path, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($Path + '.new', $Path) }
}
function Save-Journal { Save-Json $journalPath $script:journal }
function Log-Change([string]$Message) {
    Add-Content -LiteralPath (Join-Path $data 'network-actions.log') -Value ((Get-Date -Format o) + ' ' + $Message) -Encoding UTF8
}
function Check-Settings($s) {
    if ($s.Schema -ne 1 -or $s.Id -notmatch '\A[a-f0-9]{12}\z' -or $s.Role -notin @('host','client') -or $s.Version -ne '26.3') { throw 'Invalid profile.' }
    if ([int]$s.Port -lt 1024 -or [int]$s.Port -gt 65535) { throw 'Invalid port.' }
    foreach ($name in @('HostIp','FriendIp')) {
        $parsed = [ipaddress]::Parse([string]$s.$name)
        if ($parsed.AddressFamily -ne 'InterNetwork' -or [string]$s.$name -notmatch '\A10\.203\.\d{1,3}\.\d{1,3}\z') { throw 'Invalid tunnel address.' }
    }
}
function Check-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(WireGuard|Jason A\. Donenfeld)') { throw 'The WireGuard signature check failed. Installation cancelled.' }
}
function Check-TunnelConfig([string]$Path, $s) {
    foreach ($name in @('PublicKey','PeerKey')) {
        if ([string]$s.$name -notmatch '\A[A-Za-z0-9+/]{43}=\z' -or [Convert]::FromBase64String([string]$s.$name).Length -ne 32) { throw 'Invalid profile key.' }
    }
    $plain = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
    $ownIp=if($s.Role -eq 'host'){$s.HostIp}else{$s.FriendIp}
    $peerIp=if($s.Role -eq 'host'){$s.FriendIp}else{$s.HostIp}
    $pattern='\A\[Interface\]\nPrivateKey = [A-Za-z0-9+/]{43}=\nAddress = '+[regex]::Escape($ownIp)+'/32\n'
    if($s.Role -eq 'host') { $pattern+='ListenPort = '+[int]$s.Port+'\n' }
    $pattern+='\n\[Peer\]\nPublicKey = '+[regex]::Escape($s.PeerKey)+'\nAllowedIPs = '+[regex]::Escape($peerIp)+'/32\n'
    if($s.Role -eq 'client') {
        if([ipaddress]::Parse([string]$s.Endpoint).AddressFamily -ne 'InterNetwork'){throw 'The host IPv4 is required.'}
        $pattern+='Endpoint = '+[regex]::Escape($s.Endpoint)+':'+[int]$s.Port+'\nPersistentKeepalive = 25\n'
    }
    $pattern+='\z'
    if($plain -cnotmatch $pattern){throw 'The configuration contains unexpected settings; tunnel not created.'}
    $plain=$null
}
function Product-Code([string]$Msi) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($Msi,0))
    $view = $database.GetType().InvokeMember('OpenView','InvokeMethod',$null,$database,@('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''))
    $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)
    $code = $record.GetType().InvokeMember('StringData','GetProperty',$null,$record,1)
    if ($code -notmatch '\A\{[A-Fa-f0-9-]{36}\}\z') { throw 'Could not read the installer product code.' }
    return $code
}
function Remove-OwnRouterRule {
    if (!$script:journal.routerOwned) { return }
    $nat = New-Object -ComObject HNetCfg.NATUPnP
    $mappings = $nat.StaticPortMappingCollection
    if ($null -eq $mappings) { throw 'The router is not reachable over UPnP. Remove the MinePair UDP rule by hand; its details are in network-journal.json.' }
    $found = @($mappings | Where-Object { $_.ExternalPort -eq [int]$script:journal.routerPort -and $_.Protocol -eq 'UDP' })
    if ($found.Count -eq 0) { $script:journal.routerOwned=$false; Save-Journal; return }
    if ($found.Count -ne 1 -or $found[0].Description -ne $script:journal.routerDescription -or $found[0].InternalClient -ne $script:journal.routerClient -or $found[0].InternalPort -ne [int]$script:journal.routerPort) {
        throw 'The UDP rule was changed by another application. It was not removed automatically.'
    }
    $mappings.Remove([int]$script:journal.routerPort,'UDP')
    $script:journal.routerOwned=$false; Save-Journal; Log-Change 'Removed own UPnP mapping'
}
function Stop-OwnNetwork {
    $errors = @()
    if (Test-Path -LiteralPath $wg) {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            & $wg /uninstalltunnelservice $tunnel 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { $errors += 'Could not remove our WireGuard tunnel.' }
        }
    } elseif (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { $errors += 'WireGuard is missing but its service remains. Manual check required.' }
    Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    try { Remove-OwnRouterRule } catch { $errors += $_.Exception.Message }
    $script:journal.active = $false; Save-Journal; Log-Change 'Stopped own tunnel and removed own firewall rules'
    if ($errors.Count) { throw ($errors -join "`n") }
}

try {
    if (![Environment]::Is64BitProcess) { throw 'Windows x64 is required.' }
    if (!(Test-Path -LiteralPath (Join-Path $data 'settings.json'))) { throw 'No application profile.' }
    $s = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
    Check-Settings $s
    $tunnel = 'MinePair_' + $s.Id
    $serviceName = 'WireGuardTunnel$' + $tunnel
    $group = 'MinePair ' + $s.Id
    if (Test-Path -LiteralPath $journalPath) { $script:journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json }
    else { $script:journal = [pscustomobject]@{schema=1;id=$s.Id;installedWireGuard=$false;productCode='';active=$false;routerOwned=$false;routerPort=0;routerClient='';routerDescription=''} }
    if ($script:journal.schema -ne 1 -or $script:journal.id -ne $s.Id) { throw 'The journal belongs to another profile. Roll it back first.' }

    switch ($Action) {
        'install' {
            if (Test-Path -LiteralPath $wg) { Check-Signature $wg; break }
            $msi = Join-Path $data 'downloads\wireguard.msi'
            Check-Signature $msi
            $script:journal.productCode = Product-Code $msi
            $script:journal.installedWireGuard = $true; Save-Journal
            Log-Change 'Installing signed WireGuard MSI; installation ownership recorded'
            $p = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList @('/i',('"' + $msi + '"'),'/qn','/norestart','DO_NOT_LAUNCH=1') -WindowStyle Hidden -Wait -PassThru
            if ($p.ExitCode -notin @(0,3010)) { throw ('WireGuard installation: exit code ' + $p.ExitCode) }
            if (!(Test-Path -LiteralPath $wg)) { throw 'Installation not finished. Windows may need a restart.' }
            Check-Signature $wg
        }
        'start' {
            Check-Signature $wg
            if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'A previous tunnel is still there. Press "Stop / remove network" first.' }
            foreach ($address in @($s.HostIp,$s.FriendIp)) {
                if (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $address -ErrorAction SilentlyContinue) { throw ('Address already used by another adapter: ' + $address) }
            }
            if ($s.Role -eq 'host') {
                $java = [IO.Path]::GetFullPath([string]$s.Java)
                $javaRoot = [IO.Path]::GetFullPath((Join-Path $data 'java')) + '\'
                if (!$java.StartsWith($javaRoot,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $java) -or [IO.Path]::GetFileName($java) -ne 'java.exe') { throw 'Java must be inside the application folder.' }
                # Expose no management services: only the dedicated game port is permitted.
            }
            $conf = Join-Path $data ('protected\' + $tunnel + '.conf.dpapi')
            if (!(Test-Path -LiteralPath $conf)) { throw 'No protected tunnel configuration.' }
            Check-TunnelConfig $conf $s
            $script:journal.active=$true; Save-Journal; Log-Change 'Starting own tunnel; rollback intent saved'
            try {
                # Establish restrictions on the unique tunnel address BEFORE the adapter exists.
                $ownIp=if($s.Role -eq 'host'){$s.HostIp}else{$s.FriendIp}
                $common = @{Group=$group;LocalAddress=$ownIp;Direction='Inbound';Action='Block';Profile='Any';Enabled='True'}
                if ($s.Role -eq 'host') {
                    New-NetFirewallRule @common -Name ($tunnel+'-block-tcp-other') -DisplayName 'MinePair: block other TCP ports' -Protocol TCP -LocalPort @('1-25564','25566-65535') | Out-Null
                    New-NetFirewallRule @common -Name ($tunnel+'-block-udp') -DisplayName 'MinePair: block inner UDP' -Protocol UDP | Out-Null
                    New-NetFirewallRule @common -Name ($tunnel+'-block-icmp') -DisplayName 'MinePair: block inner ICMP' -Protocol ICMPv4 | Out-Null
                    New-NetFirewallRule -Name ($tunnel+'-minecraft') -DisplayName 'MinePair: Minecraft from paired friend only' -Group $group -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 25565 -LocalAddress $s.HostIp -RemoteAddress $s.FriendIp -Program $java | Out-Null
                    New-NetFirewallRule -Name ($tunnel+'-wireguard') -DisplayName 'MinePair: encrypted UDP transport' -Group $group -Direction Inbound -Action Allow -Profile Any -Protocol UDP -LocalPort ([int]$s.Port) -Program $wg | Out-Null
                } else {
                    New-NetFirewallRule @common -Name ($tunnel+'-block-inbound') -DisplayName 'MinePair: block unsolicited inbound to friend PC' -Protocol Any | Out-Null
                }
                & $wg /installtunnelservice $conf 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not create the WireGuard tunnel service.' }
                Set-Service -Name $serviceName -StartupType Manual
                $adapter = $null
                for ($i=0; $i -lt 60; $i++) { $adapter=Get-NetAdapter -Name $tunnel -ErrorAction SilentlyContinue; if($adapter) {break}; Start-Sleep -Milliseconds 250 }
                if (!$adapter) { throw 'The WireGuard network adapter did not appear.' }
                Disable-NetAdapterBinding -Name $tunnel -ComponentID ms_tcpip6 -ErrorAction Stop | Out-Null
                Get-NetFirewallRule -Group $group | Where-Object {$_.Name -ne ($tunnel+'-wireguard')} | Set-NetFirewallRule -InterfaceAlias $tunnel
                # Firewall rules exist before Java can be launched by the unelevated supervisor.
                $confDump = & $wgTool show $tunnel allowed-ips 2>&1
                if ($LASTEXITCODE -ne 0 -or ($confDump -join '') -match '0\.0\.0\.0/0|::/0') { throw 'Could not verify the restricted tunnel routes.' }
                Log-Change 'Own tunnel active with restricted /32 routes and interface-scoped firewall rules'
            } catch {
                $reason=$_.Exception.Message
                try { Stop-OwnNetwork } catch { $reason += '; rollback: ' + $_.Exception.Message }
                throw $reason
            }
        }
        'stop' { Stop-OwnNetwork }
        'status' {
            Check-Signature $wg
            $values = & $wgTool show $tunnel latest-handshakes 2>&1
            if ($LASTEXITCODE -ne 0) { throw 'The tunnel is not running or not reachable.' }
            foreach ($line in $values) {
                $parts = ([string]$line).Trim() -split '\s+'
                if ($parts.Count -eq 2 -and $parts[0] -eq $s.PeerKey) {
                    $stamp=[long]$parts[1]
                    if ($stamp -eq 0) { $handshake='No confirmed connection with the friend yet.' }
                    else { $age=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $stamp; $handshake=('Last encrypted handshake: ' + $age + ' s ago. This is not a packet-loss measurement.') }
                }
            }
            if ($null -eq $handshake) { throw 'The paired key was not found in the running tunnel.' }
        }
        'uninstall' {
            Stop-OwnNetwork
            if ($script:journal.installedWireGuard) {
                $other = @(Get-Service -Name 'WireGuardTunnel$*' -ErrorAction SilentlyContinue)
                $configDir = Join-Path $env:ProgramFiles 'WireGuard\Data\Configurations'
                $otherConfigs = @(Get-ChildItem -LiteralPath $configDir -File -ErrorAction SilentlyContinue)
                if ($other.Count -or $otherConfigs.Count) { throw 'WireGuard kept: other tunnels/configurations exist. The MinePair network itself was removed.' }
                if ($script:journal.productCode -notmatch '\A\{[A-Fa-f0-9-]{36}\}\z') { throw 'No verified product code of the installed package; WireGuard was not removed.' }
                $p = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList @('/x',$script:journal.productCode,'/qn','/norestart') -WindowStyle Hidden -Wait -PassThru
                if ($p.ExitCode -notin @(0,1605,3010)) { throw ('WireGuard removal: exit code ' + $p.ExitCode) }
                $script:journal.installedWireGuard=$false; Save-Journal; Log-Change 'Removed WireGuard installed by MinePair; user data preserved'
            }
        }
        'router-add' {
            if ($s.Role -ne 'host') { throw 'Only the server owner needs a router rule.' }
            if (!(Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { throw 'Start the secure network/server first.' }
            if ($script:journal.routerOwned) { throw 'Our rule is already recorded. Remove it first or check it on the router.' }
            $nat = New-Object -ComObject HNetCfg.NATUPnP
            $mappings = $nat.StaticPortMappingCollection
            if ($null -eq $mappings) { throw 'UPnP is not available. There is no need to enable it: add one UDP rule by hand following the "Router" instructions.' }
            $existing = @($mappings | Where-Object { $_.ExternalPort -eq [int]$s.Port -and $_.Protocol -eq 'UDP' })
            if ($existing.Count) { throw 'This UDP port is already used by a router rule. The other rule was left unchanged.' }
            $route = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 | Sort-Object RouteMetric)
            if (!$route.Count) { throw 'No route to the internet.' }
            $local = @(Get-NetIPAddress -InterfaceIndex $route[0].InterfaceIndex -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254.*' })
            if ($local.Count -ne 1) { throw 'Could not determine a single local address. Use the manual setup.' }
            $script:journal.routerPort=[int]$s.Port; $script:journal.routerClient=$local[0].IPAddress; $script:journal.routerDescription=$tunnel; $script:journal.routerOwned=$true
            Save-Journal
            $mappings.Add([int]$s.Port,'UDP',[int]$s.Port,$local[0].IPAddress,$true,$tunnel) | Out-Null
            Log-Change 'Added own UPnP UDP mapping; existing router settings preserved'
        }
        'router-remove' { Remove-OwnRouterRule }
    }
    Save-Json $resultPath @{ok=$true;action=$Action;time=(Get-Date -Format o);handshake=$handshake}
    exit 0
} catch {
    $message=$_.Exception.Message
    try { Log-Change ('Failure in ' + $Action + ': ' + $message); Save-Json $resultPath @{ok=$false;action=$Action;error=$message} } catch {}
    exit 1
}
