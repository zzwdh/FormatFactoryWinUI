use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::io::{self, BufRead, Write};

const MIB: i64 = 1 << 20;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Workload {
    weight: i32,
    estimated_peak_bytes: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Facts {
    stream_copy: bool,
    video: bool,
    audio: bool,
    document: bool,
    image: bool,
    heavy_codec: bool,
    pixels: i64,
    hdr: bool,
    pages: i32,
    file_size: i64,
}

impl Facts {
    fn estimate(&self) -> Workload {
        let bounded = self.pixels.clamp(0, 268_435_456);
        let (weight, estimated_peak_bytes) = if self.stream_copy {
            (1, 96 * MIB)
        } else if self.video {
            let base = if self.pixels >= 7680 * 4320 {
                4
            } else if self.pixels >= 3840 * 2160 {
                2
            } else {
                1
            };
            (
                base.max(if self.heavy_codec { 2 } else { 1 }),
                256 * MIB + bounded * if self.hdr { 192 } else { 128 },
            )
        } else if self.audio {
            (1, 128 * MIB)
        } else if self.document {
            (
                if self.pages >= 100 || self.file_size >= 100 * MIB {
                    2
                } else {
                    1
                },
                (384 + i64::from(self.pages.clamp(0, 1024))) * MIB,
            )
        } else if self.image {
            (
                if self.pixels >= 80_000_000 {
                    3
                } else if self.pixels >= 24_000_000 {
                    2
                } else {
                    1
                },
                64 * MIB + bounded * 32,
            )
        } else {
            (1, 256 * MIB)
        };
        Workload {
            weight,
            estimated_peak_bytes,
        }
    }
}

fn fits(
    candidate: &Workload,
    running: &[Workload],
    parallel_limit: i32,
    total: i64,
    available: i64,
) -> bool {
    if running.is_empty() {
        return true;
    }
    if running.len() >= parallel_limit as usize
        || running.iter().map(|r| r.weight.clamp(1, 4)).sum::<i32>() + candidate.weight.clamp(1, 4)
            > parallel_limit
    {
        return false;
    }
    if total <= 0 || available < 0 || available > total || candidate.estimated_peak_bytes <= 0 {
        return true;
    }
    let free = available.saturating_sub((512 * MIB).max(total / 10)).max(0);
    let budget = total - total / 3;
    let reserved = running
        .iter()
        .fold(0i64, |v, r| v + r.estimated_peak_bytes.clamp(0, budget - v));
    candidate.estimated_peak_bytes <= free.min(budget - reserved)
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Item {
    id: String,
    cost: Option<Workload>,
    source: Option<Facts>,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Snapshot {
    pending: Vec<Item>,
    running: Vec<Workload>,
    parallel_limit: i32,
    total: i64,
    available: i64,
    order: Option<Vec<String>>,
    unavailable: Vec<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct Grant {
    id: String,
    cost: Workload,
}

#[derive(Default, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Decision {
    accepted: bool,
    ordered: Option<Vec<String>>,
    grants: Vec<Grant>,
}

impl Snapshot {
    fn schedule(mut self) -> Result<Decision> {
        let mut decision = Decision::default();
        if let Some(order) = self.order {
            // Idle UI rows are valid in a publication. A row already reserved or
            // being cancelled cannot be moved back into the pending sequence.
            let unavailable: HashSet<_> = self.unavailable.into_iter().collect();
            if order.iter().any(|id| unavailable.contains(id)) {
                return Ok(decision);
            }
            let positions: HashMap<_, _> =
                order.iter().enumerate().map(|(i, id)| (id, i)).collect();
            if positions.len() != order.len() {
                return Err("duplicate-scheduler-id".into());
            }
            self.pending
                .sort_by_key(|job| positions.get(&job.id).copied().unwrap_or(usize::MAX));
            decision.ordered = Some(self.pending.into_iter().map(|job| job.id).collect());
            // Publication can invoke UI callbacks. Grant no slots until the host
            // submits the resulting snapshot after those callbacks complete.
        } else {
            let limit = self.parallel_limit.clamp(1, 4);
            let mut available = self.available;
            for item in self.pending {
                if self.running.len() >= limit as usize {
                    break;
                }
                let cost = match (item.cost, item.source) {
                    (Some(cost), None) => cost,
                    (None, Some(source)) => source.estimate(),
                    _ => return Err("invalid-scheduler-workload".into()),
                };
                if !fits(&cost, &self.running, limit, self.total, available) {
                    break;
                }
                // Grants in the same response haven't allocated memory yet. Charge
                // each new reservation against this sample as well as aggregate peak.
                if self.total > 0 && (0..=self.total).contains(&available) {
                    available = available
                        .saturating_sub(cost.estimated_peak_bytes.max(0))
                        .max(0);
                }
                self.running.push(cost.clone());
                decision.grants.push(Grant { id: item.id, cost });
            }
        }
        decision.accepted = true;
        Ok(decision)
    }
}

#[derive(Deserialize)]
#[serde(tag = "Action", rename_all_fields = "PascalCase")]
enum Request {
    Estimate(Facts),
    Admit {
        candidate: Workload,
        running: Vec<Workload>,
        parallel_limit: i32,
        total: i64,
        available: i64,
    },
    Schedule(Snapshot),
}

pub(crate) fn run() -> Result<()> {
    let mut output = io::stdout().lock();
    for line in io::stdin().lock().lines() {
        let request = serde_json::from_str(&line?).map_err(|_| "invalid-scheduler-request")?;
        let result = match request {
            Request::Estimate(source) => serde_json::to_writer(&mut output, &source.estimate()),
            Request::Admit {
                candidate,
                running,
                parallel_limit,
                total,
                available,
            } => serde_json::to_writer(
                &mut output,
                &fits(&candidate, &running, parallel_limit, total, available),
            ),
            Request::Schedule(snapshot) => {
                serde_json::to_writer(&mut output, &snapshot.schedule()?)
            }
        };
        result.map_err(io::Error::other)?;
        writeln!(output)?;
        output.flush()?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn item(id: &str, weight: i32, mb: i64) -> Item {
        Item {
            id: id.into(),
            cost: Some(Workload {
                weight,
                estimated_peak_bytes: mb * MIB,
            }),
            source: None,
        }
    }
    fn snapshot() -> Snapshot {
        Snapshot {
            pending: vec![item("a", 1, 300), item("b", 2, 500), item("c", 1, 100)],
            running: vec![],
            parallel_limit: 4,
            total: 8 * 1024 * MIB,
            available: 1500 * MIB,
            order: None,
            unavailable: vec![],
        }
    }
    #[test]
    fn batch_reserves_memory_and_preserves_head_of_line() {
        let decision = snapshot().schedule().unwrap();
        assert_eq!(
            decision
                .grants
                .into_iter()
                .map(|g| g.id)
                .collect::<Vec<_>>(),
            ["a"]
        );
        let decision = Snapshot {
            available: 8 * 1024 * MIB,
            ..snapshot()
        }
        .schedule()
        .unwrap();
        assert_eq!(decision.grants.len(), 3);
    }
    #[test]
    fn reorder_stably_keeps_omitted_rows_and_rejects_reserved_rows() {
        let decision = Snapshot {
            order: Some(vec!["idle".into(), "c".into()]),
            ..snapshot()
        }
        .schedule()
        .unwrap();
        assert_eq!(decision.ordered.unwrap(), ["c", "a", "b"]);
        assert!(decision.grants.is_empty());
        let rejected = Snapshot {
            order: Some(vec!["b".into()]),
            unavailable: vec!["b".into()],
            ..snapshot()
        }
        .schedule()
        .unwrap();
        assert!(!rejected.accepted);
    }
}
