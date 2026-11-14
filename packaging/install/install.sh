#!/bin/sh

message() { printf 'opencode-mcp: %s\n' "$*"; }
refuse() { message "$*" >&2; exit 2; }
failed() { if [ "$phase" != cleanup ]; then message "Installation failed during $phase." >&2; fi; exit 1; }
operation() { if ! "$@" 2>/dev/null; then failed; fi; }
required() { command -v "$1" >/dev/null 2>&1 || refuse "Required command is unavailable: $1."; }
line() { sed -n "${2}p" "$1"; }
valid_version() {
    printf '%s\n' "$1" | grep -Eq '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$' || return 1
    prerelease=${1%%+*}
    case $prerelease in
        *-*) prerelease=${prerelease#*-}
            printf '%s\n' "$prerelease" | tr '.' '\n' | grep -Eq '^0[0-9]+$' && return 1 ;;
    esac
    return 0
}
path_value() {
    case $2 in ''|*'
'*|*"$(printf '\r')"*) refuse "Option requires a value: $1." ;; esac
}
absolute() (
    case $1 in /*) path=$1 ;; *) path=$PWD/$1 ;; esac
    if [ -d "$path" ]; then cd -P "$path" || return 1; printf '%s\n' "$PWD"; return 0; fi
    [ ! -e "$path" ] && [ ! -L "$path" ] || return 1
    parent=$(absolute "$(dirname "$path")") || return 1
    base=$(basename "$path")
    case $base in .) printf '%s\n' "$parent" ;; ..) dirname "$parent" ;; *) printf '%s/%s\n' "${parent%/}" "$base" ;; esac
)
digest() { digest_stdin 2>/dev/null < "$1"; }
inventory() (
    cd "$1" || return 1
    find . -mindepth 1 -print | LC_ALL=C sort | while IFS= read -r item; do
        item=${item#./}
        if [ -L "$item" ]; then return 1
        elif [ -d "$item" ]; then printf 'directory  %s\n' "$item"
        elif [ -f "$item" ]; then
            hash=$(digest "$item") || return 1
            printf '%s  %s\n' "$hash" "$item"
        else return 1
        fi
    done
)
verify_payload() {
    [ -d "$payload" ] && [ ! -L "$payload" ] || refuse 'Installed files differ from the ownership record: current.'
    [ -f "$data/inventory" ] && [ ! -L "$data/inventory" ] || refuse "Install collision: $data."
    [ -x "$payload/opencode-mcp" ] || refuse 'Installed files differ from the ownership record: opencode-mcp.'
    while IFS= read -r entry; do
        expected=${entry%%  *}; relative=${entry#*  }
        case $relative in ''|/*|../*|*/../*|*/..|..) refuse "Install collision: $data." ;; esac
        item=$payload/$relative
        if [ "$expected" = directory ]; then
            [ -d "$item" ] && [ ! -L "$item" ] || refuse "Installed files differ from the ownership record: $relative."
        else
            [ -f "$item" ] && [ ! -L "$item" ] || refuse "Installed files differ from the ownership record: $relative."
            [ "$(digest "$item")" = "$expected" ] || refuse "Installed files differ from the ownership record: $relative."
        fi
    done < "$data/inventory"
    actual=$(inventory "$payload") || refuse 'Installed files differ from the ownership record: current.'
    recorded=$(cat "$data/inventory")
    if [ "$actual" != "$recorded" ]; then
        relative=$(printf '%s\n' "$actual" | while IFS= read -r entry; do
            if ! grep -Fx -e "$entry" "$data/inventory" >/dev/null; then printf '%s\n' "${entry#*  }"; break; fi
        done)
        refuse "Installed files differ from the ownership record: ${relative:-inventory}."
    fi
}
read_owner() {
    owned=0
    if [ ! -e "$data" ] && [ ! -L "$data" ]; then return 0; fi
    [ -d "$data" ] && [ ! -L "$data" ] || refuse "Install collision: $data."
    [ -f "$data/owner" ] && [ ! -L "$data/owner" ] || refuse "Install collision: $data."
    [ "$(line "$data/owner" 1)" = opencode-mcp ] && [ "$(line "$data/owner" 2)" = "$data" ] || refuse "Install collision: $data."
    recorded_link=$(line "$data/owner" 3)
    if [ "$uninstall" = 1 ] && [ "$install_given" = 0 ]; then
        link=$recorded_link; install=$(dirname "$link")
    elif [ "$recorded_link" != "$link" ]; then
        if [ "$uninstall" = 1 ]; then refuse "Installation directory differs from the ownership record: $install."
        else refuse "Install collision: $data."
        fi
    fi
    [ "$recorded_link" = "$install/opencode-mcp" ] || refuse "Install collision: $data."
    bin_created=$(line "$data/owner" 4); data_created=$(line "$data/owner" 5)
    case $bin_created:$data_created in 0:0|0:1|1:0|1:1) ;; *) refuse "Install collision: $data." ;; esac
    [ "$(wc -l < "$data/owner" | tr -d ' ')" = 5 ] || refuse "Install collision: $data."
    [ -f "$data/current" ] && [ ! -L "$data/current" ] || refuse "Install collision: $data."
    old_version=$(line "$data/current" 1); old_rid=$(line "$data/current" 2)
    valid_version "$old_version" || refuse "Install collision: $data."
    case $old_rid in linux-x64|linux-arm64|osx-x64|osx-arm64) ;; *) refuse "Install collision: $data." ;; esac
    [ "$(wc -l < "$data/current" | tr -d ' ')" = 2 ] || refuse "Install collision: $data."
    payload=$data/versions/$old_version-$old_rid
    [ -d "$data/versions" ] && [ ! -L "$data/versions" ] || refuse "Install collision: $data."
    [ -L "$link" ] && [ "$(readlink "$link")" = "$payload/opencode-mcp" ] || refuse 'Installed files differ from the ownership record: opencode-mcp.'
    verify_payload
    owned=1
}
quote_sh() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }
path_preflight() {
    path_file=; path_kind=; path_start=; path_size=; path_owned=0
    if [ ! -e "$data/path.record" ] && [ ! -L "$data/path.record" ]; then return 0; fi
    [ -f "$data/path.record" ] && [ ! -L "$data/path.record" ] || refuse "PATH ownership is ambiguous: $data/path.record."
    path_file=$(line "$data/path.record" 1); path_kind=$(line "$data/path.record" 2)
    [ -f "$path_file" ] && [ ! -L "$path_file" ] || refuse "PATH ownership is ambiguous: $path_file."
    path_owned=1
    if [ "$path_kind" = fish ]; then
        [ "$(tail -n +3 "$data/path.record" | digest_stdin)" = "$(digest "$path_file")" ] || refuse "PATH ownership is ambiguous: $path_file."
    else
        case $path_kind in 0|1) ;; *) refuse "PATH ownership is ambiguous: $path_file." ;; esac
        header="# >>> opencode-mcp $data >>>"
        count=$(grep -Fxc -e "$header" "$path_file")
        [ "$count" = 1 ] || refuse "PATH ownership is ambiguous: $path_file."
        number=$(grep -Fnx -e "$header" "$path_file" | cut -d : -f 1)
        path_start=$(head -n "$((number - 1))" "$path_file" | wc -c | tr -d ' ')
        path_size=$(tail -n +3 "$data/path.record" | wc -c | tr -d ' ')
        observed=$(dd if="$path_file" bs=1 skip="$path_start" count="$path_size" 2>/dev/null | digest_stdin)
        expected=$(tail -n +3 "$data/path.record" | digest_stdin)
        [ "$observed" = "$expected" ] || refuse "PATH ownership is ambiguous: $path_file."
    fi
}
digest_stdin() {
    if [ "$os" = Darwin ]; then hash_output=$(shasum -a 256) || return 1
    else hash_output=$(sha256sum) || return 1
    fi
    printf '%.64s\n' "$hash_output"
}
prepare_path() {
    path_changed=0
    [ "$add_path" = 1 ] && [ "$path_owned" = 0 ] || return 0
    shell=${SHELL##*/}
    case $shell in
        sh) path_file=$HOME/.profile ;;
        bash) path_file=$HOME/.profile
            for name in .bash_profile .bash_login .profile; do
                if [ -f "$HOME/$name" ]; then path_file=$HOME/$name; break; fi
            done ;;
        zsh) path_file=${ZDOTDIR:-$HOME}/.zprofile ;;
        fish) config=${XDG_CONFIG_HOME:-$HOME/.config}
            case $config in /*) ;; *) config=$HOME/.config ;; esac
            path_file=$config/fish/conf.d/opencode-mcp.fish ;;
        *) refuse "Unsupported shell for --add-to-path: $shell." ;;
    esac
    path_value --add-to-path "$path_file"
    path_parent=$(absolute "$(dirname "$path_file")") || refuse "PATH ownership is ambiguous: $path_file."
    path_file=$path_parent/$(basename "$path_file")
    case $install in *:*|*'
'*) refuse "PATH ownership is ambiguous: $path_file." ;; esac
    [ ! -L "$path_file" ] || refuse "PATH ownership is ambiguous: $path_file."
    path_kind=0
    if [ "$shell" = fish ]; then
        [ ! -e "$path_file" ] || refuse "PATH ownership is ambiguous: $path_file."
        path_kind=fish
    elif [ -e "$path_file" ]; then
        [ -f "$path_file" ] || refuse "PATH ownership is ambiguous: $path_file."
        if grep -F -e "# >>> opencode-mcp $data >>>" "$path_file" >/dev/null; then refuse "PATH ownership is ambiguous: $path_file."; fi
    else path_kind=1
    fi
    case :$PATH: in *:"$install":*) return 0 ;; esac
    path_changed=1
}
write_path() {
    [ "$path_changed" = 1 ] || return 0
    phase=path
    if [ "$path_kind" = fish ]; then
        escaped=$(printf '%s' "$install" | sed "s/\\\\/\\\\\\\\/g; s/'/\\\\'/g")
        printf "if not contains -- '%s' \$PATH; set -gx PATH '%s' \$PATH; end\n" "$escaped" "$escaped" > "$work/block" || failed
    else
        { printf '# >>> opencode-mcp %s >>>\n' "$data"
          printf "export PATH=%s:\"\$PATH\"\n" "$(quote_sh "$install")"
          printf '# <<< opencode-mcp %s <<<\n' "$data"
        } > "$work/block" || failed
    fi
    { printf '%s\n%s\n' "$path_file" "$path_kind"; cat "$work/block"; } > "$work/path.record" || failed
    operation mkdir -p "$(dirname "$path_file")"
    path_temp=$(mktemp "$work/path-XXXXXX") || failed
    if [ -f "$path_file" ]; then operation cp -p "$path_file" "$path_temp"; fi
    if [ -s "$path_temp" ] && [ -n "$(tail -c 1 "$path_temp")" ]; then printf '\n' >> "$path_temp" || failed; fi
    cat "$work/block" >> "$path_temp" || failed
    operation mv -f "$path_temp" "$path_file"
    path_temp=; path_written=1
}
remove_path() {
    [ "$path_owned" = 1 ] || return 0
    if [ "$path_kind" = fish ]; then operation rm "$path_file"
    else
        path_temp=$(mktemp "$work/path-XXXXXX") || failed
        operation cp -p "$path_file" "$path_temp"
        { dd if="$path_file" bs=1 count="$path_start" 2>/dev/null
          dd if="$path_file" bs=1 skip="$((path_start + path_size))" 2>/dev/null
        } > "$path_temp" || failed
        if [ "$path_kind" = 1 ] && [ ! -s "$path_temp" ]; then operation rm "$path_file"
        else operation mv -f "$path_temp" "$path_file"; path_temp=
        fi
    fi
}
finish() {
    result=$?
    trap - 0
    cleanup_error=0
    if [ "$result" -ne 0 ] && [ "$phase" = cleanup ]; then cleanup_error=1; fi
    if [ "$result" -ne 0 ] && [ "$uninstall_link_removed" = 1 ]; then
        ln -s "$payload/opencode-mcp" "$link" 2>/dev/null || cleanup_error=1
    fi
    if [ "$result" -ne 0 ] && [ "$activated" = 1 ]; then
        if [ "$owned" = 1 ]; then
            rollback_link=$install/.opencode-mcp-$$
            if ln -s "$payload/opencode-mcp" "$rollback_link" 2>/dev/null; then
                link_temp=$rollback_link
                if { [ ! -e "$link" ] && [ ! -L "$link" ]; } || { [ -L "$link" ] && [ ! -d "$link" ] && [ "$(readlink "$link")" = "$final/opencode-mcp" ]; }; then
                    mv -f "$link_temp" "$link" 2>/dev/null || cleanup_error=1
                else cleanup_error=1; retained=$install
                fi
            else cleanup_error=1
            fi
        else rm -f "$link" || cleanup_error=1
        fi
    fi
    if [ "$result" -ne 0 ] && [ "$records_started" = 1 ]; then
        for record in owner current inventory; do
            if [ "$owned" = 1 ]; then mv -f "$work/old-$record" "$data/$record" 2>/dev/null || cleanup_error=1
            else rm -f "$data/$record" || cleanup_error=1
            fi
        done
    fi
    if [ "$result" -ne 0 ] && [ "$path_written" = 1 ]; then
        path_owned=1
        # The record is the exact inserted block, never a whole-file restore.
        cp "$work/path.record" "$data/path.record" 2>/dev/null || cleanup_error=1
        if ! (path_preflight; remove_path) 2>/dev/null; then cleanup_error=1; fi
        rm -f "$data/path.record" || cleanup_error=1
    fi
    [ -z "$path_temp" ] || rm -f "$path_temp" || cleanup_error=1
    [ -z "$link_temp" ] || rm -f "$link_temp" || cleanup_error=1
    if [ "$cleanup_error" = 0 ] && [ "$result" -ne 0 ] && [ "$new_payload" = 1 ]; then rm -rf "$final" || { cleanup_error=1; retained=$final; }; fi
    if [ "$cleanup_error" = 0 ] && [ -n "$work" ]; then rm -rf "$work" || { cleanup_error=1; retained=$work; }; fi
    if [ "$result" -ne 0 ] && [ "$owned" = 0 ]; then
        [ ! -d "$data/versions" ] || rmdir "$data/versions" 2>/dev/null || { cleanup_error=1; retained=$data/versions; }
        [ "$data_created" = 0 ] || [ ! -d "$data" ] || rmdir "$data" 2>/dev/null || { cleanup_error=1; retained=$data; }
        [ "$bin_created" = 0 ] || [ ! -d "$install" ] || rmdir "$install" 2>/dev/null || { cleanup_error=1; retained=$install; }
    fi
    if [ "$cleanup_error" = 1 ]; then message "Cleanup failed; retained owned directory: ${retained:-$data}." >&2; [ "$result" -ne 0 ] || result=1; fi
    exit "$result"
}
installer() {
OPENCODE_MCP_RELEASE_VERSION='0.1.0-preview.1'
    version=$OPENCODE_MCP_RELEASE_VERSION
    install=${XDG_BIN_HOME:-$HOME/.local/bin}
    case $install in /*) ;; *) install=$HOME/.local/bin ;; esac
    data=${XDG_DATA_HOME:-$HOME/.local/share}
    case $data in /*) ;; *) data=$HOME/.local/share ;; esac
    data=$data/opencode-mcp
    source=; add_path=0; uninstall=0; seen=' '; install_given=0
    while [ "$#" -gt 0 ]; do
        option=$1; shift
        case $option in --version|--install-dir|--data-dir|--asset-dir|--add-to-path|--uninstall) ;; *) refuse "Unknown option: $option." ;; esac
        case $seen in *" $option "*) refuse "Option may be specified only once: $option." ;; esac
        seen="$seen$option "
        case $option in
            --add-to-path) add_path=1 ;;
            --uninstall) uninstall=1 ;;
            *) [ "$#" -gt 0 ] || refuse "Option requires a value: $option."
                path_value "$option" "$1"
                case $option in
                    --version) version=$1 ;;
                    --install-dir) install=$1; install_given=1 ;;
                    --data-dir) data=$1 ;;
                    --asset-dir) source=$1 ;;
                esac
                shift ;;
        esac
    done
    if [ "$uninstall" = 1 ]; then
        case $seen in *' --version '*|*' --asset-dir '*|*' --add-to-path '*) refuse 'Uninstall accepts only the installation directory.' ;; esac
    fi
    path_value --install-dir "$install"
    path_value --data-dir "$data"
    valid_version "$version" || refuse 'Version must be an exact SemVer 2.0.0 value without a leading v.'
    for tool in uname getconf grep sed cut tr dirname basename cat wc find sort readlink head tail dd cmp; do required "$tool"; done
    os=$(uname -s); arch=$(uname -m)
    bits=$(getconf LONG_BIT 2>/dev/null) || refuse 'A 64-bit operating system and shell process are required.'
    [ "$bits" = 64 ] || refuse 'A 64-bit operating system and shell process are required.'
    case $os in
        Linux) libc=$(getconf GNU_LIBC_VERSION 2>/dev/null) || refuse 'Linux requires glibc; musl is not supported.'
            case $libc in 'glibc '*) ;; *) refuse 'Linux requires glibc; musl is not supported.' ;; esac
            prefix=linux ;;
        Darwin) required sysctl
            native=$(sysctl -n hw.optional.arm64 2>/dev/null)
            [ "$native" != 1 ] || arch=arm64
            prefix=osx ;;
        *) refuse "Unsupported platform: $os/$arch." ;;
    esac
    case $arch in x86_64) rid=$prefix-x64 ;; arm64|aarch64) rid=$prefix-arm64 ;; *) refuse "Unsupported platform: $os/$arch." ;; esac
    if [ "$os" = Darwin ]; then required shasum; else required sha256sum; fi
    if [ "$uninstall" = 0 ]; then
        required tar
        [ -n "$source" ] || required curl
    fi
    for tool in mkdir rmdir rm mv cp ln mktemp; do required "$tool"; done
    install=$(absolute "$install") || refuse "Install collision: $install."
    data=$(absolute "$data") || refuse "Install collision: $data."
    link=$install/opencode-mcp
    case $data/ in "$install/"*) refuse "Install collision: $data." ;; esac
    case $install/ in "$data/"*) refuse "Install collision: $install." ;; esac
    owned=0; bin_created=0; data_created=0
    [ -d "$install" ] || bin_created=1
    [ -d "$data" ] || data_created=1
    read_owner
    path_preflight
    work=; link_temp=; path_temp=; path_written=0; activated=0; records_started=0; new_payload=0; uninstall_link_removed=0; retained=
    trap 'exit 130' INT HUP
    trap 'exit 143' TERM
    if [ "$uninstall" = 1 ]; then
        [ "$owned" = 1 ] || { message "No owned installation: $install"; return 0; }
        phase=uninstall
        trap finish 0
        operation rm "$link"
        uninstall_link_removed=1
        phase=path
        work=$(mktemp -d "$data/staging-XXXXXX") || failed
        remove_path
        uninstall_link_removed=0
        phase=cleanup; retained=$data
        operation rm -f "$data/path.record"
        operation rm -f "$data/owner" "$data/current" "$data/inventory"
        retained=$payload
        operation rm -rf "$payload"
        retained=$work
        operation rm -rf "$work"
        work=; retained=$data
        # Only empty installer-created containers may disappear.
        if [ -z "$(find "$data/versions" -mindepth 1 -print)" ]; then operation rmdir "$data/versions"; fi
        if [ "$data_created" = 1 ] && [ -z "$(find "$data" -mindepth 1 -print)" ]; then operation rmdir "$data"; fi
        retained=$install
        if [ "$bin_created" = 1 ] && [ -z "$(find "$install" -mindepth 1 -print)" ]; then operation rmdir "$install"; fi
        message "Uninstalled opencode-mcp: $install"; return 0
    fi
    if [ "$owned" = 0 ] && { [ -e "$link" ] || [ -L "$link" ]; }; then refuse "Install collision: $link."; fi
    resolved=$(command -v opencode-mcp 2>/dev/null) || resolved=
    if [ -n "$resolved" ]; then
        resolved_parent=$(absolute "$(dirname "$resolved")") || refuse "Install collision: $resolved."
        resolved=$resolved_parent/$(basename "$resolved")
    fi
    if [ -n "$resolved" ] && { [ "$owned" = 0 ] || [ "$resolved" != "$link" ]; }; then refuse "Install collision: $resolved."; fi
    prepare_path
    phase=staging
    trap finish 0
    operation mkdir -p "$install" "$data/versions"
    work=$(mktemp -d "$data/staging-XXXXXX") || failed
    if [ "$owned" = 1 ]; then
        for record in owner current inventory; do operation cp "$data/$record" "$work/old-$record"; done
    fi
    archive=opencode-mcp-$version-$rid.tar.gz
    phase=download
    if [ -n "$source" ]; then
        operation cp "$source/SHA256SUMS" "$work/SHA256SUMS"
        operation cp "$source/$archive" "$work/$archive"
    else
        url=https://github.com/opencode-dotnet/opencode-mcp/releases/download/v$version
        operation curl -fsSL "$url/SHA256SUMS" -o "$work/SHA256SUMS"
        operation curl -fsSL "$url/$archive" -o "$work/$archive"
    fi
    count=0; hash=
    while IFS= read -r entry || [ -n "$entry" ]; do
        case $entry in *"  $archive")
            count=$((count + 1)); hash=${entry%"  $archive"}
            if [ "${#hash}" != 64 ] || printf '%s' "$hash" | grep -q '[^0-9a-f]'; then refuse "Invalid checksum entry: $archive."; fi ;;
        esac
    done < "$work/SHA256SUMS"
    [ "$count" = 1 ] || refuse "Invalid checksum entry: $archive."
    [ "$(digest "$work/$archive")" = "$hash" ] || refuse "SHA-256 mismatch: $archive."
    phase=staging
    operation mkdir "$work/payload"
    operation tar -xzpf "$work/$archive" -C "$work/payload"
    [ -f "$work/payload/opencode-mcp" ] && [ -x "$work/payload/opencode-mcp" ] || failed
    inventory "$work/payload" > "$work/inventory" || failed
    if inventory "$work/payload" > "$work/verified"; then
        if ! cmp -s "$work/inventory" "$work/verified"; then failed; fi
    else failed
    fi
    final=$data/versions/$version-$rid
    already=0
    if [ "$owned" = 1 ] && [ "$version/$rid" = "$old_version/$old_rid" ]; then
        cmp -s "$work/inventory" "$data/inventory" || refuse 'Installed files differ from the ownership record: inventory.'
        already=1
    elif [ -e "$final" ] || [ -L "$final" ]; then refuse "Install collision: $final."
    fi
    printf 'opencode-mcp\n%s\n%s\n%s\n%s\n' "$data" "$link" "$bin_created" "$data_created" > "$work/owner" || failed
    printf '%s\n%s\n' "$version" "$rid" > "$work/current" || failed
    phase=activation
    if [ "$already" = 0 ]; then
        operation mv "$work/payload" "$final"
        new_payload=1
    fi
    records_started=1
    for record in owner current inventory; do operation mv -f "$work/$record" "$data/$record"; done
    if [ "$already" = 0 ]; then
        candidate_link=$install/.opencode-mcp-$$
        [ ! -e "$candidate_link" ] && [ ! -L "$candidate_link" ] || failed
        operation ln -s "$final/opencode-mcp" "$candidate_link"
        link_temp=$candidate_link
        if [ -e "$link" ] || [ -L "$link" ]; then
            if [ "$owned" != 1 ] || [ ! -L "$link" ] || [ -d "$link" ] || [ "$(readlink "$link")" != "$payload/opencode-mcp" ]; then refuse "Install collision: $link."; fi
        fi
        operation mv -f "$link_temp" "$link"
        link_temp=; activated=1
    fi
    write_path
    phase=activation
    if [ "$path_written" = 1 ]; then operation mv "$work/path.record" "$data/path.record"; fi
    activated=0; records_started=0; new_payload=0; path_written=0
    phase=cleanup
    retained=$payload
    if [ "$owned" = 1 ] && [ "$already" = 0 ]; then operation rm -rf "$payload"; fi
    retained=$work
    operation rm -rf "$work"
    work=
    if [ "$already" = 1 ]; then message "Already installed $version ($rid): $link"
    else message "Installed $version ($rid): $link"
    fi
    if [ "$path_changed" = 1 ]; then message 'PATH updated for future login sessions.'; else message 'PATH unchanged.'; fi
}
installer "$@"
