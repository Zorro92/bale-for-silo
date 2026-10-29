#!/usr/bin/env bash
# Run Silo's official Android TV app in an Android TV emulator, to see what Bale should look like.
#
#   scripts/silo-tv-reference.sh setup          download the emulator, an Android TV 14 image and the newest Silo TV APK
#   scripts/silo-tv-reference.sh start          boot the emulator (headless) and launch Silo
#   scripts/silo-tv-reference.sh shot out.png   screenshot (1920x1080; halve for Bale's 960x540 layout)
#   scripts/silo-tv-reference.sh key DPAD_DOWN DPAD_CENTER ...   remote keys (BACK, DPAD_LEFT, ...)
#   scripts/silo-tv-reference.sh type "south park"
#   scripts/silo-tv-reference.sh stop
#
# Needs KVM and adb. Sign the app in once with a device code like any TV.
set -euo pipefail

SDK="${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}"
AVD_HOME="${ANDROID_AVD_HOME:-$HOME/.android/avd}"
AVD=silotv
# The newest emulator build segfaulted on Fedora 44 during boot; 37.2.12 with the host GPU works.
EMULATOR_ZIP=emulator-linux_x64-16428233.zip
IMAGE_ZIP=x86-34_r03.zip
DEVICE=emulator-5554

adb_() { adb -s "$DEVICE" "$@"; }

case "${1:-}" in
  setup)
    tmp=$(mktemp -d)
    mkdir -p "$SDK/system-images/android-34/android-tv" "$SDK/platforms" "$SDK/platform-tools" "$AVD_HOME/$AVD.avd"
    curl -fsSLo "$tmp/emu.zip" "https://dl.google.com/android/repository/$EMULATOR_ZIP"
    curl -fsSLo "$tmp/img.zip" "https://dl.google.com/android/repository/sys-img/android-tv/$IMAGE_ZIP"
    rm -rf "$SDK/emulator" && unzip -q "$tmp/emu.zip" -d "$SDK"
    unzip -q -o "$tmp/img.zip" -d "$SDK/system-images/android-34/android-tv/"
    printf 'avd.ini.encoding=UTF-8\npath=%s\ntarget=android-34\n' "$AVD_HOME/$AVD.avd" > "$AVD_HOME/$AVD.ini"
    cat > "$AVD_HOME/$AVD.avd/config.ini" <<CONFIG
AvdId=$AVD
abi.type=x86
hw.cpu.arch=x86
image.sysdir.1=system-images/android-34/android-tv/x86/
tag.id=android-tv
hw.device.name=tv_1080p
hw.lcd.width=1920
hw.lcd.height=1080
hw.lcd.density=320
hw.ramSize=3072
hw.cpu.ncore=4
hw.keyboard=yes
hw.dPad=yes
disk.dataPartition.size=6G
CONFIG
    gh release download -R Silo-Server/silo-android -p 'silo-android-tv-*.apk' -D "$tmp" --clobber \
      "$(gh release list -R Silo-Server/silo-android -L 1 --json tagName --jq '.[0].tagName')"
    "$0" start
    adb_ install -r "$tmp"/silo-android-tv-*.apk
    rm -rf "$tmp"
    ;;
  start)
    if ! adb_ get-state >/dev/null 2>&1; then
      ANDROID_SDK_ROOT="$SDK" ANDROID_AVD_HOME="$AVD_HOME" setsid nohup "$SDK/emulator/emulator" -avd "$AVD" \
        -no-window -no-audio -no-snapshot -no-boot-anim -no-metrics -gpu host >/dev/null 2>&1 < /dev/null &
    fi
    adb_ wait-for-device
    until [ "$(adb_ shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ]; do sleep 2; done
    adb_ shell monkey -p org.siloserver.silo -c android.intent.category.LEANBACK_LAUNCHER 1 >/dev/null 2>&1 || true
    ;;
  shot) adb_ exec-out screencap -p > "${2:?usage: shot out.png}" ;;
  key) shift; adb_ shell input keyevent "$@" ;;
  type) adb_ shell input text "${2// /%s}" ;;
  stop) adb_ emu kill ;;
  *) sed -n '2,12p' "$0"; exit 2 ;;
esac
