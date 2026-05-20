# OpenSWFUnity

OpenSWFUnity is an experimental SWF/Flash runtime for Unity, written in pure C#.

The goal of this project is to load, inspect, and render SWF files directly inside Unity without using WebView or browser-based Flash emulation.

> This project is experimental and currently does not aim to be a full Adobe Flash Player replacement.

## Goals

- Parse SWF headers and tags
- Render Flash display objects inside Unity
- Support timeline playback
- Support basic input and audio
- Experiment with partial ActionScript support

## Non-goals for now

- Full AVM2 / ActionScript 3 support
- Perfect compatibility with all Flash games
- DRM or protected SWF support
