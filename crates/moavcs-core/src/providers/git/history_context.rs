use super::*;
use crate::models::{HistoryReference, HistoryReferenceKind};
use std::collections::{HashMap, HashSet};

const LIMIT: usize = 200;
impl GitProvider {
    pub(super) fn snapshot_impl(
        &self,
        root: &Path,
        scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        let root = self.root(root, cancel)?;
        let context = self.git_context(&root, cancel)?;
        let (refs, tips) = self.read_refs(&root, cancel)?;
        if context.is_unborn && scope == HistoryScope::CurrentBranch {
            return Ok(HistorySnapshot {
                entries: vec![],
                scope_label: "첫 커밋 전 · 현재 브랜치 이력".into(),
                git: Some(context),
                is_truncated: false,
            });
        }
        let mut unique = HashSet::new();
        let mut starts = Vec::new();
        if scope == HistoryScope::AllBranches {
            if !context.head_id.is_empty() {
                unique.insert(context.head_id.clone());
                starts.push(context.head_id.clone())
            }
            for id in tips {
                if unique.insert(id.clone()) {
                    starts.push(id)
                }
            }
        } else {
            if !context.head_id.is_empty() {
                unique.insert(context.head_id.clone());
                starts.push(context.head_id.clone())
            }
            if !context.upstream_id.is_empty() && unique.insert(context.upstream_id.clone()) {
                starts.push(context.upstream_id.clone())
            }
        }
        if starts.is_empty() {
            return Ok(HistorySnapshot {
                entries: vec![],
                scope_label: "모든 로컬·원격 추적 브랜치의 이력".into(),
                git: Some(context),
                is_truncated: false,
            });
        }
        let stdin = Some(format!("{}\n", starts.join("\n")));
        let log = self
            .run_input(
                &root,
                vec![
                    "log".into(),
                    "--stdin".into(),
                    "--topo-order".into(),
                    format!("--max-count={}", LIMIT + 1),
                    "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%s%x00".into(),
                ],
                stdin,
                cancel,
            )?
            .ensure()?;
        let mut entries = parse_log(&log.stdout)?;
        let truncated = entries.len() > LIMIT;
        entries.truncate(LIMIT);
        // git log suppresses parents at shallow boundaries; raw object headers restore the
        // known edge so the UI never mistakes a shallow commit for a true root.
        if context.is_shallow {
            for entry in &mut entries {
                if entry.parent_ids.is_empty() {
                    entry.parent_ids = self.raw_parents(&root, &entry.id, cancel)?
                }
            }
        }
        let local = if context.head_id.is_empty() {
            HashSet::new()
        } else {
            self.reachable(&root, &context.head_id, cancel)?
        };
        let upstream = if context.upstream_id.is_empty() {
            None
        } else {
            Some(self.reachable(&root, &context.upstream_id, cancel)?)
        };
        for entry in &mut entries {
            if entry.id == context.head_id {
                entry.references.push(HistoryReference {
                    name: "HEAD".into(),
                    kind: HistoryReferenceKind::Head,
                })
            }
            if let Some(found) = refs.get(&entry.id) {
                entry.references.extend(found.clone())
            }
            entry.relation = self.relation(
                &root,
                &entry.id,
                &local,
                upstream.as_ref(),
                &context,
                cancel,
            )?;
        }
        let mut label = match scope {
            HistoryScope::AllBranches => {
                "모든 로컬·원격 추적 브랜치와 태그에서 도달 가능한 이력".into()
            }
            HistoryScope::CurrentBranch
                if !context.upstream.is_empty() && !context.upstream_id.is_empty() =>
            {
                format!("현재 브랜치와 추적 브랜치 {}의 이력", context.upstream)
            }
            _ => "현재 브랜치 이력".into(),
        };
        if truncated {
            label.push_str(&format!(" · 최근 {LIMIT}개 표시"))
        }
        if context.is_shallow {
            label.push_str(" · 얕은 저장소에 내려받은 범위")
        }
        Ok(HistorySnapshot {
            entries,
            scope_label: label,
            git: Some(context),
            is_truncated: truncated,
        })
    }
    fn git_context(&self, root: &Path, cancel: &CancelToken) -> Result<GitHistoryContext> {
        let shallow = self
            .run(
                root,
                vec!["rev-parse".into(), "--is-shallow-repository".into()],
                cancel,
            )?
            .stdout
            .trim()
            == "true";
        let symbolic = self.run(
            root,
            vec!["symbolic-ref".into(), "-q".into(), "HEAD".into()],
            cancel,
        )?;
        let branch = if symbolic.success() {
            symbolic
                .stdout
                .trim()
                .strip_prefix("refs/heads/")
                .unwrap_or(symbolic.stdout.trim())
                .to_string()
        } else {
            String::new()
        };
        let h = self.run(
            root,
            vec![
                "rev-parse".into(),
                "--verify".into(),
                "HEAD^{commit}".into(),
            ],
            cancel,
        )?;
        let head = if h.success() {
            h.stdout.trim().to_string()
        } else {
            String::new()
        };
        let unborn = head.is_empty();
        let detached = !symbolic.success() && !unborn;
        let mut c = GitHistoryContext {
            current_branch: branch,
            is_detached: detached,
            is_unborn: unborn,
            is_shallow: shallow,
            head_id: head.clone(),
            status: if unborn {
                "첫 커밋 전"
            } else if detached {
                "분리된 HEAD"
            } else {
                "추적 브랜치 없음"
            }
            .into(),
            ..Default::default()
        };
        if !unborn && !detached {
            let (up, upid, push, pushid) = self.branch_refs(root, &c.current_branch, cancel)?;
            c.upstream = up;
            c.upstream_id = upid;
            c.push_target = push;
            c.push_target_id = pushid;
            if c.upstream.is_empty() {
                c.status = "추적 브랜치 없음".into()
            } else if c.upstream_id.is_empty() {
                c.status = "추적 브랜치를 로컬에서 찾을 수 없음 · 가져오기 필요".into()
            } else if shallow {
                c.status = "얕은 저장소 · 내려받은 범위 밖의 차이는 계산하지 않음".into()
            } else {
                let r = self.run(
                    root,
                    vec![
                        "rev-list".into(),
                        "--left-right".into(),
                        "--count".into(),
                        format!("{}...{}", c.upstream_id, head),
                    ],
                    cancel,
                )?;
                if r.success() {
                    let f: Vec<_> = r.stdout.split_whitespace().collect();
                    if f.len() == 2 {
                        if let (Ok(behind), Ok(ahead)) = (f[0].parse(), f[1].parse()) {
                            c.behind = Some(behind);
                            c.ahead = Some(ahead);
                            c.status = if ahead == 0 && behind == 0 {
                                "추적 브랜치와 동기화됨"
                            } else if ahead > 0 && behind > 0 {
                                "로컬·추적 브랜치가 서로 다름"
                            } else if ahead > 0 {
                                "추적 브랜치에 없는 로컬 커밋 있음"
                            } else {
                                "현재 브랜치에 없는 추적 커밋 있음"
                            }
                            .into()
                        }
                    }
                }
            }
        }
        Ok(c)
    }
    fn branch_refs(
        &self,
        root: &Path,
        branch: &str,
        cancel: &CancelToken,
    ) -> Result<(String, String, String, String)> {
        let r = self.run(
            root,
            vec![
                "for-each-ref".into(),
                "--format=%(upstream)%00%(upstream:short)%00%(push)%00%(push:short)%00".into(),
                format!("refs/heads/{branch}"),
            ],
            cancel,
        )?;
        if !r.success() {
            return Ok(Default::default());
        }
        let f: Vec<_> = r
            .stdout
            .trim_end_matches(['\r', '\n'])
            .split('\0')
            .collect();
        if f.len() < 4 {
            return Ok(Default::default());
        }
        let up = f[1].to_owned();
        let push = f[3].to_owned();
        let upid = self.ref_oid(root, f[0], cancel)?;
        let pushid = self.ref_oid(root, f[2], cancel)?;
        Ok((up, upid, push, pushid))
    }
    fn ref_oid(&self, root: &Path, reference: &str, cancel: &CancelToken) -> Result<String> {
        if !reference.starts_with("refs/") {
            return Ok(String::new());
        }
        let r = self.run(
            root,
            vec![
                "rev-parse".into(),
                "--verify".into(),
                format!("{reference}^{{commit}}"),
            ],
            cancel,
        )?;
        Ok(if r.success() {
            r.stdout.trim().to_owned()
        } else {
            String::new()
        })
    }
    fn read_refs(
        &self,
        root: &Path,
        cancel: &CancelToken,
    ) -> Result<(HashMap<String, Vec<HistoryReference>>, Vec<String>)> {
        let r=self.required(root,vec!["for-each-ref".into(),"--format=%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00%(*objecttype)%00".into()],cancel)?;
        let mut refs: HashMap<String, Vec<HistoryReference>> = HashMap::new();
        let mut tips = HashSet::new();
        for line in r.stdout.split('\n').filter(|l| !l.is_empty()) {
            let f: Vec<_> = line.trim_end_matches('\r').split('\0').collect();
            if f.len() < 5 {
                continue;
            }
            let (kind, name) = if let Some(n) = f[0].strip_prefix("refs/heads/") {
                (HistoryReferenceKind::LocalBranch, n)
            } else if let Some(n) = f[0].strip_prefix("refs/remotes/") {
                (HistoryReferenceKind::RemoteBranch, n)
            } else if let Some(n) = f[0].strip_prefix("refs/tags/") {
                (HistoryReferenceKind::Tag, n)
            } else {
                continue;
            };
            let oid = if f[2] == "commit" {
                f[1]
            } else if f[4] == "commit" {
                f[3]
            } else {
                continue;
            };
            if !is_oid(oid) {
                continue;
            }
            tips.insert(oid.to_owned());
            refs.entry(oid.into()).or_default().push(HistoryReference {
                name: name.into(),
                kind,
            });
        }
        let mut tips: Vec<_> = tips.into_iter().collect();
        tips.sort();
        Ok((refs, tips))
    }
    fn reachable(&self, root: &Path, tip: &str, cancel: &CancelToken) -> Result<HashSet<String>> {
        let r = self.run(
            root,
            vec!["rev-list".into(), "--max-count=50000".into(), tip.into()],
            cancel,
        )?;
        if !r.success() {
            return Ok(HashSet::new());
        }
        Ok(r.stdout
            .lines()
            .map(str::trim)
            .filter(|s| is_oid(s))
            .map(str::to_owned)
            .collect())
    }
    fn raw_parents(&self, root: &Path, id: &str, cancel: &CancelToken) -> Result<Vec<String>> {
        let out = self.required(
            root,
            vec!["cat-file".into(), "-p".into(), id.into()],
            cancel,
        )?;
        Ok(out
            .stdout
            .lines()
            .take_while(|l| !l.is_empty())
            .filter_map(|l| l.strip_prefix("parent "))
            .filter(|p| is_oid(p))
            .map(str::to_owned)
            .collect())
    }
    fn relation(
        &self,
        root: &Path,
        id: &str,
        local: &HashSet<String>,
        upstream: Option<&HashSet<String>>,
        context: &GitHistoryContext,
        cancel: &CancelToken,
    ) -> Result<HistoryRelation> {
        if context.is_shallow || upstream.is_none() {
            return Ok(HistoryRelation::Unknown);
        }
        let mut l = local.contains(id);
        if !l && !context.head_id.is_empty() {
            l = self.ancestor(root, id, &context.head_id, cancel)?
        }
        let up = upstream.unwrap();
        let mut u = up.contains(id);
        if !u && !context.upstream_id.is_empty() {
            u = self.ancestor(root, id, &context.upstream_id, cancel)?
        }
        Ok(match (l, u) {
            (true, true) => HistoryRelation::Shared,
            (true, false) => HistoryRelation::LocalOnly,
            (false, true) => HistoryRelation::UpstreamOnly,
            _ => HistoryRelation::Other,
        })
    }
    fn ancestor(
        &self,
        root: &Path,
        candidate: &str,
        tip: &str,
        cancel: &CancelToken,
    ) -> Result<bool> {
        let r = self.run(
            root,
            vec![
                "merge-base".into(),
                "--is-ancestor".into(),
                candidate.into(),
                tip.into(),
            ],
            cancel,
        )?;
        match r.exit_code {
            0 => Ok(true),
            1 => Ok(false),
            _ => r.ensure().map(|_| false),
        }
    }
}
fn parse_log(output: &str) -> Result<Vec<HistoryEntry>> {
    let f: Vec<_> = output.split('\0').collect();
    let mut rows = Vec::new();
    let mut i = 0;
    while i + 8 < f.len() {
        let id = f[i].trim_start_matches(['\r', '\n']);
        if !id.is_empty() {
            if !is_oid(id) {
                return Err(VcsError("Git 이력 ID 형식이 잘못되었습니다.".into()));
            }
            rows.push(HistoryEntry {
                id: id.into(),
                parent_ids: f[i + 1].split_whitespace().map(str::to_owned).collect(),
                author: f[i + 2].into(),
                author_email: f[i + 3].into(),
                date: f[i + 4].into(),
                committer: f[i + 5].into(),
                committer_email: f[i + 6].into(),
                commit_date: f[i + 7].into(),
                message: f[i + 8].trim_end_matches(['\r', '\n']).into(),
                ..Default::default()
            })
        }
        i += 9;
    }
    Ok(rows)
}
fn is_oid(s: &str) -> bool {
    (s.len() == 40 || s.len() == 64) && s.bytes().all(|b| b.is_ascii_hexdigit())
}
