# DriveGram — stored formats (StorageFormat 2)

Everything a drive needs lives in two private Telegram channels. This document is enough to
read a drive back **without DriveGram**, given the drive password. Anything not described here
(the local SQLite index, caches, staging) can be rebuilt from the channels.

## Channels

| Channel | Title | Holds |
|---|---|---|
| Journal | `DriveGram Journal · <name>` (drives before 0.7: `DriveGram Journal`) | pinned **control record** (plain text), journal entries `j-<device>.bin`, snapshots `s-<upto>.bin` |
| Data | `DriveGram Data · <name>` (before 0.7: `DriveGram Data`) | one document per stored chunk, file name `<first 32 hex of chunk id>.bin` |

Which data channel belongs to a journal: `control.drive.dataChannel` (0.7+). Older drives get
it written on their first writable mount; until then, the data channel created closest in time.

## Control record (pinned text message in the journal channel, JSON, camelCase)

```json
{ "v": 1,
  "vault": { "v": 1, "salt": "<base64 16B>", "iter": 600000, "chunkSize": 33554432, "frameSize": 262144, "verifier": "<hex>" },
  "lease": { "dev": "<device id>", "name": "<PC name>", "until": <unix seconds> } | null,
  "snap":  { "msg": <journal message id>, "upto": <journal message id> } | null,
  "drive": { "id": "<32 hex>", "name": "<display name>", "dataChannel": <channel id> } | null,
  "minReader": 1, "minWriter": 1,
  "handover": { "dev": "<requesting device>", "name": "<PC name>", "at": <unix>, "state": "asked|finishing|done|refused",
                "pending": <files still uploading on the holder>, "updated": <unix> } | null }
```

`handover` (1.0.3+): a request to move the lease. The requester writes `asked`; the holder answers
`refused`, or `finishing` (it takes no new changes, uploads and journals what it has, refreshing
`pending`/`updated` every ~10 s) and then writes the requester into `lease` with `done`. Without an
answer within 60 s, or when `updated` is older than 2 minutes, the requester takes the lease itself.
Older clients keep the field untouched and never answer. Adding it changed no minReader/minWriter.

`minReader` / `minWriter`: the oldest StorageFormat allowed to read / write the drive. A client
whose `StorageFormat.Current` is lower must refuse to open (reader) or open read-only (writer).

## Keys

The **master key** never changes for a drive. How to obtain it:

- vault `v: 1` (every drive until its password is changed): `master = PBKDF2-HMAC-SHA256(password UTF-8, vault.salt, vault.iter, 32 bytes)`
- vault `v: 2` (after a password change; `salt`/`iter` unused): master is **wrapped** in `vault.password`
- either version may also hold `vault.recovery`: the master wrapped under the recovery key

A wrap is `{ "salt", "iter", "nonce", "wrapped" }` (base64): `wrapped = AES-256-GCM(KEK, nonce, aad = "drivegram/wrap/v1")(master) || tag`, where

```
password KEK = PBKDF2-HMAC-SHA256(password UTF-8, wrap.salt, wrap.iter, 32 bytes)
recovery KEK = HMAC-SHA256(key = 32 raw recovery-key bytes, "drivegram/recovery/v1" || wrap.salt)
recovery key text = Crockford base32 (0-9 A-Z without I L O U) of the 32 raw bytes, 52 chars, groups of 4
```

Changing the password or adding a recovery key re-wraps the same master: no stored file changes.
A password change also sets `minReader: 2` (versions before 1.0 cannot read a v2 vault).

```
master     = (see above)
verifier   = hex(HMAC-SHA256(master, "drivegram/verify/v1"))          -- must equal vault.verifier
K_id       = HMAC-SHA256(master, "drivegram/chunk-id/v1")
K_key      = HMAC-SHA256(master, "drivegram/chunk-key/v1")
K_journal  = HMAC-SHA256(master, "drivegram/journal/v1")
chunk id   = hex(HMAC-SHA256(K_id,  SHA256(plaintext chunk)))          -- 64 hex
chunk key  = HMAC-SHA256(K_key, SHA256(plaintext chunk))               -- AES-256 key
```

## Chunk documents (data channel)

```
"DGC1" | frame_size (4 bytes big-endian)
frame i = AES-256-GCM(chunk key, nonce = 12-byte big-endian i, aad = bytes of chunk id (32 bytes))
          over plaintext bytes [i*frame_size, (i+1)*frame_size)  →  ciphertext || 16-byte tag
```

Frames are independent, so any byte range can be decrypted alone. A file is the concatenation
of its chunks in order; every chunk except possibly the last is exactly `chunkSize` bytes.

## Journal entries and snapshots (journal channel documents)

Envelope: `"DGE1" | nonce (12 random bytes) | AES-256-GCM(K_journal, nonce, aad = "DGE1")(plaintext) || tag`.

Journal entry plaintext (UTF-8 JSON): `{ "v": 1, "dev": "<device id>", "ts": <unix>, "ops": [ ... ] }`.
Apply entries in message-id order. Each op carries the full resulting state of what it touches:

| `t` | Fields | Meaning |
|---|---|---|
| `up` | id, parent, name, dir, attrs, ctime, mtime | create / rename / move / attributes (times: Windows FILETIME UTC) |
| `ct` | id, size, mtime, chunks: `[{ "c": chunk id, "m": data message id, "z": size, "h": sha256 hex }]` | new content of a file |
| `tr` | id, at | moved to the drive's trash |
| `rs` | id, parent, name | restored from the trash |
| `pg` | id | deleted forever (with its subtree) |

The root folder has id `root`. A later `ct` for the same chunk id may name a different message
(identical bytes re-uploaded after the old copy was garbage-collected): the latest one wins.

Snapshot plaintext: gzip of JSON `{ "nodes": [...], "fileChunks": [[node, idx, chunk], ...], "chunks": [...], "v": 1 }`
— the whole tree as of journal message `upto`; replay entries after `upto` on top.

## Reading a drive by hand

1. Read the control record; obtain `master` (password or recovery key, see Keys); check the verifier.
2. Load the snapshot named by `snap.msg` (if any), then apply every `j-*.bin` with a larger id.
3. For each file, download its chunks' data-channel messages, decrypt frame by frame, concatenate.
