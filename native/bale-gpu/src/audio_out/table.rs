//! Live outputs by small id: 1-65535 (never 0), so an id fits the 32-bit FFmpegOptions value the
//! FFmpegInteropX fork is handed. Ids go round: a destroyed id comes back only after the counter has passed
//! every other id, so a stale id held by a late caller names nothing rather than someone else's output.

use std::sync::Arc;

/// The largest id.
pub const MAX_ID: u32 = 65_535;

pub struct IdTable<T> {
    next: u32,
    limit: usize,
    entries: Vec<(u32, Arc<T>)>,
}

impl<T> IdTable<T> {
    /// An empty table that holds at most `limit` entries.
    pub const fn new(limit: usize) -> Self {
        Self {
            next: 1,
            limit,
            entries: Vec::new(),
        }
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn is_full(&self) -> bool {
        self.entries.len() >= self.limit
    }

    /// Registers `value` under the first free id from the one after the last handed out (65535 wraps to 1);
    /// `None` when the table is full.
    pub fn insert(&mut self, value: Arc<T>) -> Option<u32> {
        if self.is_full() {
            return None;
        }
        let mut id = self.next;
        for _ in 0..MAX_ID {
            let next = if id == MAX_ID { 1 } else { id + 1 };
            if !self.entries.iter().any(|(used, _)| *used == id) {
                self.entries.push((id, value));
                self.next = next;
                return Some(id);
            }
            id = next;
        }
        None
    }

    pub fn get(&self, id: u32) -> Option<Arc<T>> {
        self.entries
            .iter()
            .find(|(used, _)| *used == id)
            .map(|(_, value)| Arc::clone(value))
    }

    pub fn remove(&mut self, id: u32) -> Option<Arc<T>> {
        let index = self.entries.iter().position(|(used, _)| *used == id)?;
        Some(self.entries.swap_remove(index).1)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_count_up_and_are_not_reused_until_the_counter_wraps() {
        let mut table = IdTable::new(4);
        assert!(table.is_empty());
        let a = table.insert(Arc::new("a")).unwrap();
        let b = table.insert(Arc::new("b")).unwrap();
        assert_eq!((a, b), (1, 2));
        assert_eq!(table.remove(a).as_deref(), Some(&"a"));
        assert!(table.remove(a).is_none(), "destroyed twice");
        assert!(table.get(a).is_none());
        // The freed id 1 is not handed out again right away.
        assert_eq!(table.insert(Arc::new("c")), Some(3));
        assert_eq!(table.get(b).as_deref(), Some(&"b"));
        assert_eq!(table.len(), 2);
        assert!(table.get(0).is_none());
    }

    #[test]
    fn the_counter_wraps_past_live_ids_and_a_full_table_refuses() {
        let mut table = IdTable::new(3);
        table.next = MAX_ID - 1;
        assert_eq!(table.insert(Arc::new(1)), Some(MAX_ID - 1));
        assert_eq!(table.insert(Arc::new(2)), Some(MAX_ID));
        assert_eq!(
            table.insert(Arc::new(3)),
            Some(1),
            "65535 wraps to 1, never 0"
        );
        assert!(table.is_full());
        assert_eq!(table.insert(Arc::new(4)), None);
        // After the wrap a live id is skipped: 1 is still in use, so the next free one is 2.
        table.remove(MAX_ID);
        table.next = 1;
        assert_eq!(table.insert(Arc::new(5)), Some(2));
        assert_eq!(*table.get(1).unwrap(), 3);
    }
}
