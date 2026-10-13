import json
with open("memory_stream.json", "r") as f:
    memories = json.load(f)
for m in memories:
    print(f"[{m['type']}] {m['content']} (importance: {m['importance']})")