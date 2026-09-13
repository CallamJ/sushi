#!/usr/bin/env bash
set -euo pipefail

# --- class: Person ---

    # method: Person.birthday()
    _m_Person_birthday() {
        local -n this="$1"
        this['age']=$(( ${this['age']:-0} + 1 ))
        return 0
    }

    # method: Person.describe()
    _m_Person_describe() {
        local -n out="$1"
        local -n this="$2"
        out="${this['name']-} is ${this['age']-}"
    }

    # method: Person.copy()
    _m_Person_copy() {
        local -n out="$1"
        local -n this="$2"
        local -A _object1=()
        person_new '_object1' "${this['name']-}" "${this['age']-}"
        out=()
        for _key in "${!_object1[@]}"; do
            out["$_key"]="${_object1[$_key]}"
        done
    }

    # adapter: Person.string()
    person_string() {
        local -n out="$1"
        local -n this="$2"
        out="${this['name']-}"
    }

    # constructor: Person(name, age)
    person_new() {
        local -n this="$1"
        local name="$2"
        local -i age="$3"
        this=(['name']="${name:-}" ['age']="${age:-}")
        this['name']="${name:-}"
        this['age']="${age:-}"
    }

# --- enum: Priority ---

    # method: Priority.isUrgent()
    _m_Priority_isUrgent() {
        local -n out="$1"
        local -n this="$2"
        local _tmp2="${this['weight']-}"
        local _tmp3=2
        if (( _tmp2 > _tmp3 )); then
            out='true'
        else
            out='false'
        fi
    }

    # adapter: Priority.string()
    priority_string() {
        local -n out="$1"
        local -n this="$2"
        out="${this['_name']-}"
    }

    # enum value: Priority.Low
    declare -A Priority_Low=(
        ['_name']='Low'
        ['_ord']=0
        ['_value']=0
        ['weight']=1
    )

    # enum value: Priority.Normal
    declare -A Priority_Normal=(
        ['_name']='Normal'
        ['_ord']=1
        ['_value']=1
        ['weight']=2
    )

    # enum value: Priority.High
    declare -A Priority_High=(
        ['_name']='High'
        ['_ord']=2
        ['_value']=2
        ['weight']=3
    )

# --- enum: Result ---

    # adapter: Result.string()
    result_string() {
        local -n out="$1"
        local -n this="$2"
        out="${this['message']-}"
    }

    # constructor: Result(message)
    result_new() {
        local -n this="$1"
        local message="$2"
        this=(
            ['_name']="${3:-}"
            ['_ord']="${4:-}"
            ['_value']="${5:-}"
        )
        this['message']="${message:-}"
    }

    # enum value: Result.Ok
    declare -A Result_Ok=()
    result_new 'Result_Ok' 'completed' 'Ok' 0 0

    # enum value: Result.Error
    declare -A Result_Error=()
    result_new 'Result_Error' 'failed' 'Error' 1 1

# --- enum: ExitCode ---

    # enum value: ExitCode.Ok
    declare -A ExitCode_Ok=(
        ['_name']='Ok'
        ['_ord']=0
        ['_value']=0
    )

    # enum value: ExitCode.Failed
    declare -A ExitCode_Failed=(
        ['_name']='Failed'
        ['_ord']=1
        ['_value']=1
    )

# --- enum: Axis ---

    # enum value: Axis.X
    declare -A Axis_X=(
        ['_name']='X'
        ['_ord']=0
        ['_value']=0
        ['offset']=10
    )

    # enum value: Axis.Y
    declare -A Axis_Y=(
        ['_name']='Y'
        ['_ord']=1
        ['_value']=1
        ['offset']=20
    )

# function: identity(value)
identity() {
    local -n out="$1"
    local -n value="$2"
    out=()
    for _key in "${!value[@]}"; do
        out["$_key"]="${value[$_key]}"
    done
}
declare -A person=()
person_new 'person' 'Ada' 36

# --- script body ---
_m_Person_birthday 'person'
_m_Person_describe '_tmp4' 'person'
printf '%s\n' "${_tmp4-}"
person_string '_tmp5' 'person'
printf '%s\n' "${_tmp5-}"
printf '%s\n' "${Priority_High['_name']-}"
printf '%s\n' "${Priority_High['weight']-}"
_m_Priority_isUrgent '_tmp6' 'Priority_High'
printf '%s\n' "${_tmp6-}"
priority_string '_tmp7' 'Priority_Normal'
printf '%s\n' "${_tmp7-}"
[[ "${Priority_High['_ord']-}" == "${Priority_High['_ord']-}" ]] && printf '%s\n' 'true' || printf '%s\n' 'false'
[[ "${Priority_Low['_ord']-}" != "${Priority_High['_ord']-}" ]] && printf '%s\n' 'true' || printf '%s\n' 'false'
result_string '_tmp8' 'Result_Ok'
printf '%s\n' "${_tmp8-}"
printf '%s\n' "${ExitCode_Failed['_value']-}"
printf '%s\n' "${Axis_Y['offset']-}"
declare -A returned=()
identity 'returned' 'person'
printf '%s\n' "${returned['name']-}"
declare -n alias='person'
_m_Person_birthday 'person'
printf '%s\n' "${person['age']-}"
declare -A _object10=()
person_new '_object10' 'Grace' 1
person_string '_tmp9' '_object10'
printf '%s\n' "${_tmp9-}"
declare -A copy=()
_m_Person_copy 'copy' 'person'
_m_Person_describe '_tmp11' 'copy'
printf '%s\n' "${_tmp11-}"

