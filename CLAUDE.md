@AGENTS.md

## Claude desktop only

- **The Claude desktop app is an MSIX package.** Whatever its shell starts writes AppData and HKCU into a private copy (`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`), and the shell sees the merged view.
  - So a Mizar started from Claude's shell uses a **different** data folder than the owner's.
  - `Mizar.exe --register` run from Claude's shell lands in a registry Windows ignores.
  - Self-tests with `MIZAR_DATA_DIR` are redirected too, but consistently, so they work.
  - Publishing to `%LOCALAPPDATA%\Programs\Mizar` reaches the real folder.
- **To run Mizar for the owner, go through Explorer:** `explorer.exe "<path>\Mizar.exe"`.
  - With arguments (`--netdiag`, `--register`), use a `.lnk` that carries them. A `.cmd` that launches Mizar did nothing.
- **To read the owner's real files** (for example `diag.log`): have Explorer run a `.cmd` that copies them to `F:\`, then read the copy. Ask the owner to delete copies of private data afterwards: deleting files in the root of `F:\` is blocked for Claude.
- **Leftover:** the redirected `...\LocalCache\Local\Mizar` holds data (cookies, logins, history) from Mizar sessions Claude started. It is not the owner's profile.
- **Patching code:** Bash heredocs with mixed quotes have failed here. Write the patch script (Python, `python -I`) with the Write tool, or use Edit.
