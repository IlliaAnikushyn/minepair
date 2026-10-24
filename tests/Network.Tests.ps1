# Checks the journal and router logic of src/Network.ps1 without touching the system:
# only the script's function definitions are loaded, and the UPnP router is an object in memory.
param([Parameter(Mandatory=$true)][string]$Script)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

class FakeRouter : System.Collections.IEnumerable {
    [System.Collections.ArrayList]$Items = [System.Collections.ArrayList]::new()
    [bool]$Refuse = $false
    [System.Collections.IEnumerator] GetEnumerator() { return $this.Items.GetEnumerator() }
    [object] Add([int]$external, [string]$protocol, [int]$internal, [string]$client, [bool]$enabled, [string]$description) {
        if ($this.Refuse) { throw 'The router refused the mapping.' }
        $mapping = [pscustomobject]@{ ExternalPort = $external; Protocol = $protocol; InternalPort = $internal; InternalClient = $client; Description = $description }
        [void]$this.Items.Add($mapping)
        return $mapping
    }
    [void] Remove([int]$external, [string]$protocol) {
        foreach ($m in @($this.Items)) { if ($m.ExternalPort -eq $external -and $m.Protocol -eq $protocol) { $this.Items.Remove($m) } }
    }
}

$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$errors)
if ($errors.Count) { throw 'Network.ps1 does not parse.' }
foreach ($f in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($f.Extent.Text))
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('minepair-network-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$journalPath = Join-Path $temp 'network-journal.json'
$script:router = $null
$script:failures = 0

function New-Object([string]$ComObject) { [pscustomobject]@{ StaticPortMappingCollection = $script:router } }
function Physical-LanAddresses { '192.168.1.20' }
function Log-Change([string]$Message) { }
function Assert([bool]$Condition, [string]$Label) {
    if ($Condition) { 'PASS: ' + $Label } else { 'FAIL: ' + $Label; $script:failures++ }
}
function Reset-State {
    $script:journal = [pscustomobject]@{schema=1;id='a1b2c3d4e5f6';installedWireGuard=$false;productCode='';active=$false;routerOwned=$false;routerWanted=$false;routerPort=0;routerClient='';routerDescription=''}
    $script:router = [FakeRouter]::new()
}

$s = [pscustomobject]@{ Port = 51820 }
$n = Names 'a1b2c3d4e5f6'

try {
    Reset-State
    Add-OwnRouterRule $s $n
    Assert ($script:router.Items.Count -eq 1 -and $script:journal.routerOwned -and $script:router.Items[0].InternalClient -eq '192.168.1.20') 'UPnP rule created and recorded in the journal'

    Remove-OwnRouterRule
    Add-OwnRouterRule $s $n
    Assert ($script:router.Items.Count -eq 1 -and $script:journal.routerOwned) 'rule added again after a stop removed it'

    Reset-State
    $script:router.Refuse = $true
    try { Add-OwnRouterRule $s $n } catch { }
    $script:router.Refuse = $false
    Add-OwnRouterRule $s $n
    Assert ($script:router.Items.Count -eq 1 -and $script:journal.routerOwned) 'retry after a refused attempt really creates the rule'

    Reset-State
    [void]$script:router.Add(51820, 'UDP', 51820, '192.168.1.99', $true, 'Someone else')
    $refused = $false
    try { Add-OwnRouterRule $s $n } catch { $refused = $true }
    Assert ($refused -and $script:router.Items.Count -eq 1 -and $script:router.Items[0].Description -eq 'Someone else') 'a rule of another application is left alone'

    Reset-State
    Add-OwnRouterRule $s $n
    $script:router.Items[0].Description = 'Changed by the user'
    $refused = $false
    try { Remove-OwnRouterRule } catch { $refused = $true }
    Assert ($refused -and $script:router.Items.Count -eq 1) 'a rule changed by someone else is not removed'

    Reset-State
    $script:router = $null
    Remove-OwnRouterRule
    Assert (!$script:journal.routerOwned) 'nothing to remove when no rule is owned, even without UPnP'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
if ($script:failures) { exit 1 }
