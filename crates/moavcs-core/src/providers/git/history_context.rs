use super::*;
use crate::models::{HistoryReference, HistoryReferenceKind};
use std::collections::{HashMap, HashSet};

const LIMIT: usize = 200;
const REACHABILITY_LIMIT: usize = 50_000;

struct Reachability {
    ids: HashSet<String>,
    complete: bool,
}
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
            if !context.push_target_id.is_empty() && unique.insert(context.push_target_id.clone()) {
                starts.push(context.push_target_id.clone())
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
            Reachability {
                ids: HashSet::new(),
                complete: true,
            }
        } else {
            self.reachable(&root, &context.head_id, cancel)?
                .unwrap_or(Reachability {
                    ids: HashSet::new(),
                    complete: false,
                })
        };
        let upstream = if context.upstream_id.is_empty() {
            None
        } else {
            self.reachable(&root, &context.upstream_id, cancel)?
        };
        let push_target = if context.push_target_id.is_empty() || context.is_shallow {
            None
        } else if context.push_target_id == context.upstream_id {
            None
        } else {
            self.reachable(&root, &context.push_target_id, cancel)?
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
            entry.push_relation = if !context.push_target_id.is_empty()
                && context.push_target_id == context.upstream_id
            {
                (entry.relation != HistoryRelation::Unknown).then_some(entry.relation)
            } else {
                self.push_relation(
                    &root,
                    &entry.id,
                    &local,
                    push_target.as_ref(),
                    &context,
                    cancel,
                )?
            };
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
    pub(super) fn git_context(
        &self,
        root: &Path,
        cancel: &CancelToken,
    ) -> Result<GitHistoryContext> {
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
            let (up, upid, push_remote, push_ref, push, pushid) =
                self.branch_refs(root, &c.current_branch, cancel)?;
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
                if let Some((behind, ahead)) =
                    self.revision_counts(root, &c.upstream_id, &head, cancel)?
                {
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
            let push_comparable = !push_ref.is_empty()
                && self.push_destination_is_fetch_remote(root, &push_remote, cancel)?;
            if !push_comparable {
                // The configured destination may be a different server than this local
                // remote-tracking ref. Do not expose that ref as if it were the push tip.
                c.push_target_id.clear();
            }
            if push_comparable
                && !c.push_target_id.is_empty()
                && !shallow
                && c.push_target_id != c.upstream_id
            {
                if let Some((behind, ahead)) =
                    self.revision_counts(root, &c.push_target_id, &head, cancel)?
                {
                    c.push_behind = Some(behind);
                    c.push_ahead = Some(ahead);
                }
            } else if push_comparable
                && !c.push_target_id.is_empty()
                && c.push_target_id == c.upstream_id
            {
                c.push_behind = c.behind;
                c.push_ahead = c.ahead;
            }
        }
        Ok(c)
    }
    fn revision_counts(
        &self,
        root: &Path,
        reference: &str,
        head: &str,
        cancel: &CancelToken,
    ) -> Result<Option<(u64, u64)>> {
        let result = self.run(
            root,
            vec![
                "rev-list".into(),
                "--left-right".into(),
                "--count".into(),
                format!("{reference}...{head}"),
            ],
            cancel,
        )?;
        if !result.success() {
            return Ok(None);
        }
        let fields: Vec<_> = result.stdout.split_whitespace().collect();
        if fields.len() != 2 {
            return Ok(None);
        }
        Ok(fields[0].parse().ok().zip(fields[1].parse().ok()))
    }
    /// A remote-tracking ref is a valid local approximation of the push destination only
    /// when that remote has one fetch URL and one identical push URL. Otherwise counts would
    /// describe a different or ambiguous server, so leave them unknown.
    fn push_destination_is_fetch_remote(
        &self,
        root: &Path,
        remote: &str,
        cancel: &CancelToken,
    ) -> Result<bool> {
        if remote.is_empty() {
            return Ok(false);
        }
        let push_refspecs = self.run(
            root,
            vec![
                "config".into(),
                "--get-all".into(),
                format!("remote.{remote}.push"),
            ],
            cancel,
        )?;
        if push_refspecs.success() || push_refspecs.exit_code != 1 {
            return Ok(false);
        }
        let fetch_urls = self.run(
            root,
            vec![
                "remote".into(),
                "get-url".into(),
                "--all".into(),
                (*remote).into(),
            ],
            cancel,
        )?;
        let push_urls = self.run(
            root,
            vec![
                "remote".into(),
                "get-url".into(),
                "--push".into(),
                "--all".into(),
                (*remote).into(),
            ],
            cancel,
        )?;
        if !fetch_urls.success() || !push_urls.success() {
            return Ok(false);
        }
        let fetch: Vec<_> = fetch_urls
            .stdout
            .lines()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .collect();
        let push: Vec<_> = push_urls
            .stdout
            .lines()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .collect();
        Ok(fetch.len() == 1 && push.len() == 1 && fetch[0] == push[0])
    }
    fn branch_refs(
        &self,
        root: &Path,
        branch: &str,
        cancel: &CancelToken,
    ) -> Result<(String, String, String, String, String, String)> {
        let r = self.run(
            root,
            vec![
                "for-each-ref".into(),
                "--format=%(upstream)%00%(upstream:short)%00%(push:remotename)%00%(push)%00%(push:short)%00".into(),
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
        if f.len() < 6 {
            return Ok(Default::default());
        }
        let up = f[1].to_owned();
        let push_remote = f[2].to_owned();
        let push = f[4].to_owned();
        let upid = self.ref_oid(root, f[0], cancel)?;
        let pushid = self.ref_oid(root, f[3], cancel)?;
        Ok((up, upid, push_remote, f[3].to_owned(), push, pushid))
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
    fn reachable(
        &self,
        root: &Path,
        tip: &str,
        cancel: &CancelToken,
    ) -> Result<Option<Reachability>> {
        let r = self.run(
            root,
            vec![
                "rev-list".into(),
                format!("--max-count={REACHABILITY_LIMIT}"),
                tip.into(),
            ],
            cancel,
        )?;
        if !r.success() {
            return Ok(None);
        }
        let ids: HashSet<_> = r
            .stdout
            .lines()
            .map(str::trim)
            .filter(|s| is_oid(s))
            .map(str::to_owned)
            .collect();
        let complete = ids.len() < REACHABILITY_LIMIT;
        Ok(Some(Reachability { ids, complete }))
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
        local: &Reachability,
        upstream: Option<&Reachability>,
        context: &GitHistoryContext,
        cancel: &CancelToken,
    ) -> Result<HistoryRelation> {
        if context.is_shallow || upstream.is_none() || !local.complete && local.ids.is_empty() {
            return Ok(HistoryRelation::Unknown);
        }
        let mut l = local.ids.contains(id);
        if !l && !local.complete && !context.head_id.is_empty() {
            l = self.ancestor(root, id, &context.head_id, cancel)?
        }
        let up = upstream.unwrap();
        let mut u = up.ids.contains(id);
        if !u && !up.complete && !context.upstream_id.is_empty() {
            u = self.ancestor(root, id, &context.upstream_id, cancel)?
        }
        Ok(match (l, u) {
            (true, true) => HistoryRelation::Shared,
            (true, false) => HistoryRelation::LocalOnly,
            (false, true) => HistoryRelation::UpstreamOnly,
            _ => HistoryRelation::Other,
        })
    }
    fn push_relation(
        &self,
        root: &Path,
        id: &str,
        local: &Reachability,
        push_target: Option<&Reachability>,
        context: &GitHistoryContext,
        cancel: &CancelToken,
    ) -> Result<Option<HistoryRelation>> {
        if context.is_shallow || context.push_target_id.is_empty() || push_target.is_none() {
            return Ok(None);
        }
        let push = push_target.unwrap();
        if !local.complete && local.ids.is_empty() || !push.complete && push.ids.is_empty() {
            return Ok(None);
        }
        let local_has = local.ids.contains(id)
            || (!local.complete
                && !context.head_id.is_empty()
                && self.ancestor(root, id, &context.head_id, cancel)?);
        let push_has = push.ids.contains(id)
            || (!push.complete && self.ancestor(root, id, &context.push_target_id, cancel)?);
        Ok(Some(match (local_has, push_has) {
            (true, true) => HistoryRelation::Shared,
            (true, false) => HistoryRelation::LocalOnly,
            (false, true) => HistoryRelation::UpstreamOnly,
            _ => HistoryRelation::Other,
        }))
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
