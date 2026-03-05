# 🔥 Master Git Command Table (VS Code Terminal)

| What You Want To Do | Windows PowerShell | Mac Terminal (bash/zsh) | Notes |
|---------------------|--------------------|--------------------------|-------|
| 🔴 **IMPORTANT – Check repo state** | `git status` | Same | Shows changed, staged, untracked files |
| 🔴 **IMPORTANT – See tracking + ahead/behind** | `git branch -vv` | Same | Shows upstream + ahead/behind |
| 🔴 **IMPORTANT – Clean stale remote refs** | `git fetch --prune` | Same | Removes deleted remote branches locally |
| 🔴 **IMPORTANT – View full commit graph** | `git log --oneline --graph --decorate --all` | Same | Best branch visualization tool |
| 🔴 **IMPORTANT – Push new branch properly** | `git push -u origin HEAD` | Same | Sets upstream tracking |
| See current branch | `git branch --show-current` | Same | Quick branch check |
| List local branches | `git branch` | Same | `*` = current |
| List remote branches | `git branch -r` | Same | Shows `origin/...` |
| List all branches | `git branch -a` | Same | Local + remote |
| Create new branch | `git checkout -b feature/x` | Same | Creates + switches |
| Switch branch | `git checkout develop` | Same | Or `git switch develop` |
| Stage all changes | `git add -A` | Same | Stages everything |
| Commit | `git commit -m "message"` | Same | Local snapshot |
| Pull latest | `git pull` | Same | Fetch + merge |
| Fetch without merge | `git fetch` | Same | Safer than pull |
| Delete local branch (safe) | `git branch -d feature/x` | Same | Only if merged |
| Force delete local branch | `git branch -D feature/x` | Same | Even if not merged |
| Delete remote branch | `git push origin --delete feature/x` | Same | Removes from GitHub |
| Stash changes | `git stash -u` | Same | Saves uncommitted work |
| Restore file | `git restore file.cs` | Same | Discards file changes |
| **Delete ALL local `feature/*` branches (safe)** | `git branch --list "feature/*" \| Where-Object { $_ -notmatch "\*" } \| ForEach-Object { git branch -d $_.Trim() }` | `git branch --list "feature/*" \| xargs -r git branch -d` | Deletes merged feature branches only |
| **Force delete ALL local `feature/*` branches** | Replace `-d` with `-D` in command above | Replace `-d` with `-D` | ⚠ Deletes even if not merged |

---

## 🧠 The 5 Commands That Solve 90% of Git Confusion

```bash
git status
git branch -vv
git fetch --prune
git log --oneline --graph --decorate --all
git push -u origin HEAD