# Runs only as SYSTEM through the MinePair helper service, from its copy in Program Files.
# -Data is the user's profile folder (untrusted input, validated here); -State is the admin-only ProgramData folder.
param(
    [Parameter(Mandatory=$true)][string]$Data,
    [Parameter(Mandatory=$true)][string]$State,
    [Parameter(Mandatory=$true)][ValidateSet('install','start','stop','uninstall','router-add','router-remove')][string]$Action
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security
$resultPath = Join-Path $State 'result.json'
$journalPath = Join-Path $State 'network-journal.json'
$tunnelDir = Join-Path $State 'tunnels'
$wg = Join-Path $env:ProgramFiles 'WireGuard\wireguard.exe'
$wgTool = Join-Path $env:ProgramFiles 'WireGuard\wg.exe'
# Exact WireGuard LLC EV certificate subject (company registration number pinned).
$signerPattern = '\ACN=WireGuard LLC, O=WireGuard LLC, .*SERIALNUMBER=4227913\z'
$script:journal = $null
$script:warning = $null

function Save-Json([string]$Path, $Value) {
    $text = $Value | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($Path + '.new', $text, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($Path + '.new', $Path, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($Path + '.new', $Path) }
}
function Save-Journal { Save-Json $journalPath $script:journal }
function Log-Change([string]$Message) {
    Add-Content -LiteralPath (Join-Path $State 'network-actions.log') -Value ((Get-Date -Format o) + ' ' + $Message) -Encoding UTF8
}
function Names([string]$Id) {
    $t = 'MinePair_' + $Id
    return [pscustomobject]@{ Tunnel = $t; Service = 'WireGuardTunnel$' + $t; Group = 'MinePair ' + $Id }
}
function Check-Settings($s) {
    if ($s.Schema -ne 2 -or [string]$s.Id -notmatch '\A[a-f0-9]{12}\z' -or $s.Role -notin @('host','client')) { throw 'Invalid profile.' }
    if ([int]$s.Port -lt 1024 -or [int]$s.Port -gt 65535) { throw 'Invalid port.' }
    $h = ([string]$s.HostIp).Split('.'); $f = ([string]$s.FriendIp).Split('.')
    if ([string]$s.HostIp -notmatch '\A10\.203\.\d{1,3}\.\d{1,3}\z' -or [string]$s.FriendIp -notmatch '\A10\.203\.\d{1,3}\.\d{1,3}\z') { throw 'Invalid tunnel address.' }
    if ([int]$h[2] -lt 16 -or [int]$h[2] -gt 239 -or [int]$h[3] % 4 -ne 1 -or $f[2] -ne $h[2] -or [int]$f[3] -ne [int]$h[3] + 1) { throw 'Invalid tunnel address.' }
}
function Check-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch $signerPattern) { throw 'The WireGuard signature check failed. Installation cancelled.' }
}
function Check-TunnelConfig([byte[]]$Bytes, $s) {
    foreach ($name in @('PublicKey','PeerKey')) {
        if ([string]$s.$name -notmatch '\A[A-Za-z0-9+/]{43}=\z' -or [Convert]::FromBase64String([string]$s.$name).Length -ne 32) { throw 'Invalid profile key.' }
    }
    $plain = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($Bytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
    $ownIp = if ($s.Role -eq 'host') { $s.HostIp } else { $s.FriendIp }
    $peerIp = if ($s.Role -eq 'host') { $s.FriendIp } else { $s.HostIp }
    $pattern = '\A\[Interface\]\nPrivateKey = [A-Za-z0-9+/]{43}=\nAddress = ' + [regex]::Escape($ownIp) + '/32\n'
    if ($s.Role -eq 'host') { $pattern += 'ListenPort = ' + [int]$s.Port + '\n' }
    $pattern += '\n\[Peer\]\nPublicKey = ' + [regex]::Escape($s.PeerKey) + '\nAllowedIPs = ' + [regex]::Escape($peerIp) + '/32\n'
    if ($s.Role -eq 'client') {
        if ([ipaddress]::Parse([string]$s.Endpoint).AddressFamily -ne 'InterNetwork') { throw 'The host IPv4 is required.' }
        $pattern += 'Endpoint = ' + [regex]::Escape($s.Endpoint) + ':' + [int]$s.Port + '\nPersistentKeepalive = 25\n'
    }
    $pattern += '\z'
    if ($plain -cnotmatch $pattern) { throw 'The configuration contains unexpected settings; tunnel not created.' }
    $plain = $null
}
function Product-Code([string]$Msi) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($Msi,0))
    $view = $database.GetType().InvokeMember('OpenView','InvokeMethod',$null,$database,@('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''))
    $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)
    $code = $record.GetType().InvokeMember('StringData','GetProperty',$null,$record,1)
    $view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null) | Out-Null
    if ($code -notmatch '\A\{[A-Fa-f0-9-]{36}\}\z') { throw 'Could not read the installer product code.' }
    return $code
}
function Remove-OwnRouterRule {
    if (!$script:journal.routerOwned) { return }
    $nat = New-Object -ComObject HNetCfg.NATUPnP
    $mappings = $nat.StaticPortMappingCollection
    if ($null -eq $mappings) { throw 'The router is not reachable over UPnP. Remove the MinePair UDP rule on the router by hand.' }
    $found = @($mappings | Where-Object { $_.ExternalPort -eq [int]$script:journal.routerPort -and $_.Protocol -eq 'UDP' })
    if ($found.Count -eq 0) { $script:journal.routerOwned = $false; Save-Journal; return }
    if ($found.Count -ne 1 -or $found[0].Description -ne $script:journal.routerDescription -or $found[0].InternalClient -ne $script:journal.routerClient -or $found[0].InternalPort -ne [int]$script:journal.routerPort) {
        throw 'The UDP rule was changed by another application. It was not removed automatically.'
    }
    $mappings.Remove([int]$script:journal.routerPort, 'UDP')
    $script:journal.routerOwned = $false; Save-Journal; Log-Change 'Removed own UPnP mapping'
}
function Add-OwnRouterRule($s, $n) {
    # The journal records intent before the router call, so it may claim a rule that was never created.
    Remove-OwnRouterRule
    $nat = New-Object -ComObject HNetCfg.NATUPnP
    $mappings = $nat.StaticPortMappingCollection
    if ($null -eq $mappings) { throw 'UPnP is off on the router. Add one UDP rule by hand following the instructions.' }
    $existing = @($mappings | Where-Object { $_.ExternalPort -eq [int]$s.Port -and $_.Protocol -eq 'UDP' })
    if ($existing.Count) { throw 'This UDP port is already used by a router rule. The other rule was left unchanged.' }
    $local = @(Physical-LanAddresses)
    if ($local.Count -ne 1) { throw 'Could not determine a single local address. Add the rule by hand.' }
    $script:journal.routerPort = [int]$s.Port; $script:journal.routerClient = $local[0]; $script:journal.routerDescription = $n.Tunnel; $script:journal.routerOwned = $true
    Save-Journal
    $mappings.Add([int]$s.Port, 'UDP', [int]$s.Port, $local[0], $true, $n.Tunnel) | Out-Null
    Log-Change 'Added own UPnP UDP mapping'
}
# Removes the tunnel service, its protected config copy and the firewall group for one profile id.
function Remove-OwnTunnel([string]$Id) {
    $n = Names $Id
    $errors = @()
    if (Get-Service -Name $n.Service -ErrorAction SilentlyContinue) {
        if (Test-Path -LiteralPath $wg) {
            & $wg /uninstalltunnelservice $n.Tunnel 2>&1 | Out-Null
            for ($i = 0; $i -lt 40 -and (Get-Service -Name $n.Service -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
        }
        if (Get-Service -Name $n.Service -ErrorAction SilentlyContinue) { $errors += 'Could not remove the WireGuard tunnel.' }
    }
    Get-NetFirewallRule -Group $n.Group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    $copy = Join-Path $tunnelDir ($n.Tunnel + '.conf.dpapi')
    if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Force }
    return $errors
}
function Stop-OwnNetwork {
    $errors = @(Remove-OwnTunnel $script:journal.id)
    try { Remove-OwnRouterRule } catch { $errors += $_.Exception.Message }
    $script:journal.active = $false; Save-Journal; Log-Change 'Stopped own tunnel and removed own firewall rules'
    if ($errors.Count) { throw ($errors -join "`n") }
}
function Physical-LanAddresses {
    $result = @()
    foreach ($adapter in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' })) {
        $config = Get-NetIPConfiguration -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue
        if ($null -eq $config -or $null -eq $config.IPv4DefaultGateway) { continue }
        $result += @($config.IPv4Address | Where-Object { $_.IPAddress -notlike '169.254.*' } | ForEach-Object { $_.IPAddress })
    }
    return $result
}

try {
    if (![Environment]::Is64BitProcess) { throw 'Windows x64 is required.' }
    $settingsPath = Join-Path $Data 'settings.json'
    if (!(Test-Path -LiteralPath $settingsPath)) { throw 'No application profile.' }
    $s = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check-Settings $s
    New-Item -ItemType Directory -Force -Path $tunnelDir | Out-Null
    if (Test-Path -LiteralPath $journalPath) { $script:journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json }
    else { $script:journal = [pscustomobject]@{schema=1;id=$s.Id;installedWireGuard=$false;productCode='';active=$false;routerOwned=$false;routerWanted=$false;routerPort=0;routerClient='';routerDescription=''} }
    if ($script:journal.schema -ne 1 -or [string]$script:journal.id -notmatch '\A[a-f0-9]{12}\z') { throw 'The network journal is damaged.' }
    # Journals from 0.3.1 and earlier: a rule that exists now was added through UPnP on purpose.
    if (!$script:journal.PSObject.Properties['routerWanted']) { $script:journal | Add-Member -NotePropertyName routerWanted -NotePropertyValue ([bool]$script:journal.routerOwned) }
    # The friend adopts the host's profile id when pairing: clean up anything left under the old id, then follow the new one.
    if ($script:journal.id -ne $s.Id) {
        if ($script:journal.active) { Stop-OwnNetwork }
        if ($script:journal.routerOwned) { throw 'Remove the previous router rule first.' }
        $script:journal.id = $s.Id; Save-Journal
    }
    $n = Names $s.Id

    switch ($Action) {
        'install' {
            if (Test-Path -LiteralPath $wg) { Check-Signature $wg; break }
            $downloads = Join-Path $State 'downloads'
            New-Item -ItemType Directory -Force -Path $downloads | Out-Null
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $index = (Invoke-WebRequest -UseBasicParsing -Uri 'https://download.wireguard.com/windows-client/').Content
            $found = @([regex]::Matches($index, 'wireguard-amd64-([0-9]+\.[0-9]+\.[0-9]+)\.msi') | ForEach-Object { [pscustomobject]@{ Name = $_.Value; Version = [version]$_.Groups[1].Value } } | Sort-Object Version -Descending)
            if ($found.Count -eq 0) { throw 'Official WireGuard installer not found.' }
            $msi = Join-Path $downloads 'wireguard.msi'
            Invoke-WebRequest -UseBasicParsing -Uri ('https://download.wireguard.com/windows-client/' + $found[0].Name) -OutFile $msi
            Check-Signature $msi
            $script:journal.productCode = Product-Code $msi
            $script:journal.installedWireGuard = $true; Save-Journal
            Log-Change 'Installing signed WireGuard MSI; installation ownership recorded'
            $p = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList @('/i',('"' + $msi + '"'),'/qn','/norestart','DO_NOT_LAUNCH=1') -WindowStyle Hidden -Wait -PassThru
            Remove-Item -LiteralPath $msi -Force -ErrorAction SilentlyContinue
            if ($p.ExitCode -notin @(0,3010)) { throw ('WireGuard installation: exit code ' + $p.ExitCode) }
            if (!(Test-Path -LiteralPath $wg)) { throw 'Installation not finished. Windows may need a restart.' }
            Check-Signature $wg
        }
        'start' {
            Check-Signature $wg
            # A leftover from a crash is cleaned up instead of blocking the start.
            $leftover = @(Remove-OwnTunnel $s.Id)
            if ($leftover.Count) { throw ($leftover -join "`n") }
            foreach ($address in @($s.HostIp,$s.FriendIp)) {
                if (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $address -ErrorAction SilentlyContinue) { throw ('Address already used by another adapter: ' + $address) }
            }
            $java = $null
            if ($s.Role -eq 'host') {
                $java = [IO.Path]::GetFullPath([string]$s.Java)
                $javaRoot = [IO.Path]::GetFullPath((Join-Path $Data 'java')) + '\'
                if (!$java.StartsWith($javaRoot,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $java -PathType Leaf) -or [IO.Path]::GetFileName($java) -ne 'java.exe') { throw 'Java must be inside the MinePair folder.' }
            }
            # Read the user's config once, validate those exact bytes, and install from an admin-only copy.
            $source = Join-Path $Data ('protected\' + $n.Tunnel + '.conf.dpapi')
            if (!(Test-Path -LiteralPath $source)) { throw 'No protected tunnel configuration.' }
            $bytes = [IO.File]::ReadAllBytes($source)
            Check-TunnelConfig $bytes $s
            $conf = Join-Path $tunnelDir ($n.Tunnel + '.conf.dpapi')
            [IO.File]::WriteAllBytes($conf, $bytes)
            $script:journal.active = $true; Save-Journal; Log-Change 'Starting own tunnel; rollback intent saved'
            try {
                # Restrictions on the tunnel address exist BEFORE the adapter does.
                $ownIp = if ($s.Role -eq 'host') { $s.HostIp } else { $s.FriendIp }
                $common = @{Group=$n.Group;LocalAddress=$ownIp;Direction='Inbound';Action='Block';Profile='Any';Enabled='True'}
                if ($s.Role -eq 'host') {
                    New-NetFirewallRule @common -Name ($n.Tunnel+'-block-tcp-other') -DisplayName 'MinePair: block other TCP ports' -Protocol TCP -LocalPort @('1-25564','25566-65535') | Out-Null
                    New-NetFirewallRule @common -Name ($n.Tunnel+'-block-udp') -DisplayName 'MinePair: block inner UDP' -Protocol UDP | Out-Null
                    New-NetFirewallRule @common -Name ($n.Tunnel+'-block-icmp') -DisplayName 'MinePair: block inner ICMP' -Protocol ICMPv4 | Out-Null
                    New-NetFirewallRule -Name ($n.Tunnel+'-minecraft') -DisplayName 'MinePair: Minecraft from paired friend only' -Group $n.Group -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 25565 -LocalAddress $s.HostIp -RemoteAddress $s.FriendIp -Program $java | Out-Null
                    New-NetFirewallRule -Name ($n.Tunnel+'-wireguard') -DisplayName 'MinePair: encrypted UDP transport' -Group $n.Group -Direction Inbound -Action Allow -Profile Any -Protocol UDP -LocalPort ([int]$s.Port) -Program $wg | Out-Null
                } else {
                    New-NetFirewallRule @common -Name ($n.Tunnel+'-block-inbound') -DisplayName 'MinePair: block unsolicited inbound to friend PC' -Protocol Any | Out-Null
                }
                & $wg /installtunnelservice $conf 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not create the WireGuard tunnel.' }
                Set-Service -Name $n.Service -StartupType Manual
                $adapter = $null
                for ($i = 0; $i -lt 60; $i++) { $adapter = Get-NetAdapter -Name $n.Tunnel -ErrorAction SilentlyContinue; if ($adapter) { break }; Start-Sleep -Milliseconds 250 }
                if (!$adapter) { throw 'The WireGuard network adapter did not appear.' }
                Disable-NetAdapterBinding -Name $n.Tunnel -ComponentID ms_tcpip6 -ErrorAction Stop | Out-Null
                Get-NetFirewallRule -Group $n.Group | Where-Object { $_.Name -ne ($n.Tunnel+'-wireguard') } | Set-NetFirewallRule -InterfaceAlias $n.Tunnel
                $routes = & $wgTool show $n.Tunnel allowed-ips 2>&1
                if ($LASTEXITCODE -ne 0 -or ($routes -join '') -match '0\.0\.0\.0/0|::/0') { throw 'Could not verify the restricted tunnel routes.' }
                Log-Change 'Own tunnel active with restricted /32 routes and interface-scoped firewall rules'
                # The host may have added a manual rule meanwhile, so a router failure warns instead of blocking the game.
                if ($s.Role -eq 'host' -and $script:journal.routerWanted) {
                    try { Add-OwnRouterRule $s $n }
                    catch { $script:warning = 'The router did not open the port automatically: ' + $_.Exception.Message; Log-Change $script:warning }
                }
            } catch {
                $reason = $_.Exception.Message
                try { Stop-OwnNetwork } catch { $reason += '; rollback: ' + $_.Exception.Message }
                throw $reason
            }
        }
        'stop' { Stop-OwnNetwork }
        'uninstall' {
            Stop-OwnNetwork
            if ($script:journal.installedWireGuard) {
                $other = @(Get-Service -Name 'WireGuardTunnel$*' -ErrorAction SilentlyContinue)
                $otherConfigs = @(Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'WireGuard\Data\Configurations') -File -ErrorAction SilentlyContinue)
                if ($other.Count -or $otherConfigs.Count) { throw 'WireGuard kept: you have other WireGuard tunnels. The MinePair network was removed.' }
                $code = [string]$script:journal.productCode
                if ($code -notmatch '\A\{[A-Fa-f0-9-]{36}\}\z') { throw 'No verified product code of the installed package; WireGuard was not removed.' }
                $entry = Get-ItemProperty -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $code) -ErrorAction SilentlyContinue
                $displayName = if ($null -ne $entry -and $entry.PSObject.Properties['DisplayName']) { [string]$entry.DisplayName } else { '' }
                if ($displayName -notlike 'WireGuard*') { throw 'The recorded package is not WireGuard; nothing was removed.' }
                $p = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList @('/x',$code,'/qn','/norestart') -WindowStyle Hidden -Wait -PassThru
                if ($p.ExitCode -notin @(0,1605,3010)) { throw ('WireGuard removal: exit code ' + $p.ExitCode) }
                $script:journal.installedWireGuard = $false; Save-Journal; Log-Change 'Removed WireGuard installed by MinePair; user data preserved'
            }
        }
        'router-add' {
            if ($s.Role -ne 'host') { throw 'Only the host needs a router rule.' }
            # Wanted for every later start too: stop removes the rule, start puts it back.
            $script:journal.routerWanted = $true; Save-Journal
            Add-OwnRouterRule $s $n
        }
        'router-remove' {
            $script:journal.routerWanted = $false; Save-Journal
            Remove-OwnRouterRule
        }
    }
    Save-Json $resultPath @{ ok = $true; action = $Action; time = (Get-Date -Format o); warning = $script:warning }
    exit 0
} catch {
    $message = $_.Exception.Message
    try { Log-Change ('Failure in ' + $Action + ': ' + $message); Save-Json $resultPath @{ ok = $false; action = $Action; error = $message } } catch {}
    exit 1
}
