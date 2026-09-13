Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

enum ExitCode {
    Ok = 0
    Failed = 1
}

class Priority {
    [object] $weight
    [object] $_name
    [object] $_ord
    Priority([object]$weight, [object]$_name, [object]$_ord) {
        $this.weight = $weight
        $this._name = $_name
        $this._ord = $_ord
    }
    [object] isUrgent() {
        return ($this.weight -gt 2)
    }
    [string] ToString() {
        return $this._name
    }
    static [Priority] $Low = [Priority]::new(1, 'Low', 0)
    static [Priority] $Normal = [Priority]::new(2, 'Normal', 1)
    static [Priority] $High = [Priority]::new(3, 'High', 2)
}

class Result {
    [object] $message
    [object] $_name
    [object] $_ord
    Result([object]$message, [object]$_name, [object]$_ord) {
        $this._name = $_name
        $this._ord = $_ord
        ($this).message = $message
    }
    [string] ToString() {
        return $this.message
    }
    static [Result] $Ok = [Result]::new('completed', 'Ok', 0)
    static [Result] $Error = [Result]::new('failed', 'Error', 1)
}

class Axis {
    [object] $offset
    [object] $_name
    [object] $_ord
    Axis([object]$offset, [object]$_name, [object]$_ord) {
        $this.offset = $offset
        $this._name = $_name
        $this._ord = $_ord
    }
    static [Axis] $X = [Axis]::new(10, 'X', 0)
    static [Axis] $Y = [Axis]::new(20, 'Y', 1)
}

class Person {
    [string] $name
    [int] $age
    Person([string]$name, [int]$age) {
        ($this).name = $name
        ($this).age = $age
    }
    [void] birthday() {
        ($this).age += 1
    }
    [string] describe() {
        return (($this.name + ' is ') + $this.age)
    }
    [Person] copy() {
        return [Person]::new($this.name, $this.age)
    }
    [string] ToString() {
        return $this.name
    }
}

function identity {
    param([Person]$value)
    return $value
}
$person = [Person]::new('Ada', 36)
$person.birthday()
Write-Output ($person.describe())
Write-Output ($person.ToString())
Write-Output ([Priority]::High)._name
Write-Output ([Priority]::High).weight
Write-Output (([Priority]::High).isUrgent())
Write-Output (([Priority]::Normal).ToString())
Write-Output (([Priority]::High)._ord -eq ([Priority]::High)._ord)
Write-Output (([Priority]::Low)._ord -ne ([Priority]::High)._ord)
Write-Output (([Result]::Ok).ToString())
Write-Output ([int]([ExitCode]::Failed))
Write-Output ([Axis]::Y).offset
$returned = (identity $person)
Write-Output $returned.name
$alias = $person
$alias.birthday()
Write-Output $person.age
Write-Output (([Person]::new('Grace', 1)).ToString())
$copy = ($person.copy())
Write-Output ($copy.describe())
