#!/usr/bin/env bash
# SteamGridDB Fetcher - SteamOS / Steam Deck launcher (run in Desktop Mode)
cd "$(dirname "$0")"
exec python3 sgdb_fetcher_steamos.py "$@"
