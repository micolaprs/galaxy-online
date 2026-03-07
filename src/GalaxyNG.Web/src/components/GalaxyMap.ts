export interface MapPlanet {
  name: string;
  x: number;
  y: number;
  owner: 'mine' | 'enemy' | 'neutral';
  size: number;
  hasShips?: boolean;
}

export interface MapLine { x1: number; y1: number; x2: number; y2: number; color: string; }

const COLORS = {
  mine:    '#4ade80',
  enemy:   '#f87171',
  neutral: '#94a3b8',
  bg:      '#0f172a',
  grid:    '#1e293b',
  text:    '#e2e8f0',
  select:  '#facc15',
  ship:    '#38bdf8',
};

export class GalaxyMap {
  private canvas: HTMLCanvasElement;
  private ctx:    CanvasRenderingContext2D;
  private galaxySize = 200;
  private planets: MapPlanet[] = [];
  private lines:   MapLine[]   = [];
  private selected: string | null = null;

  // Pan / zoom
  private scale  = 1;
  private panX   = 0;
  private panY   = 0;
  private dragging = false;
  private dragStart = { x: 0, y: 0, panX: 0, panY: 0 };

  onPlanetClick?: (name: string) => void;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.ctx    = canvas.getContext('2d')!;
    this.setupEvents();
  }

  setData(galaxySize: number, planets: MapPlanet[], lines: MapLine[] = []): void {
    this.galaxySize = galaxySize;
    this.planets    = planets;
    this.lines      = lines;
    this.fitToView();
    this.draw();
  }

  select(name: string | null): void {
    this.selected = name;
    this.draw();
  }

  private fitToView(): void {
    const pad  = 40;
    const size = Math.min(this.canvas.width, this.canvas.height) - pad * 2;
    this.scale = size / this.galaxySize;
    this.panX  = pad;
    this.panY  = pad;
  }

  draw(): void {
    const { ctx, canvas } = this;
    ctx.fillStyle = COLORS.bg;
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    // Grid
    ctx.strokeStyle = COLORS.grid;
    ctx.lineWidth   = 0.5;
    const step = this.toScreen(20) - this.panX; // 20 ly grid
    for (let x = this.panX % step; x < canvas.width; x += step) {
      ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, canvas.height); ctx.stroke();
    }
    for (let y = this.panY % step; y < canvas.height; y += step) {
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(canvas.width, y); ctx.stroke();
    }

    // Lines (routes / movement)
    for (const l of this.lines) {
      const [sx, sy] = this.worldToScreen(l.x1, l.y1);
      const [ex, ey] = this.worldToScreen(l.x2, l.y2);
      ctx.strokeStyle = l.color;
      ctx.lineWidth   = 1;
      ctx.setLineDash([4, 4]);
      ctx.beginPath(); ctx.moveTo(sx, sy); ctx.lineTo(ex, ey); ctx.stroke();
      ctx.setLineDash([]);
    }

    // Planets
    for (const p of this.planets) {
      const [sx, sy] = this.worldToScreen(p.x, p.y);
      const r        = Math.max(3, Math.min(8, (p.size / 1000) * 12));
      const isSelected = p.name === this.selected;

      if (isSelected) {
        ctx.strokeStyle = COLORS.select;
        ctx.lineWidth   = 2;
        ctx.beginPath();
        ctx.arc(sx, sy, r + 4, 0, Math.PI * 2);
        ctx.stroke();
      }

      ctx.fillStyle = COLORS[p.owner];
      ctx.beginPath();
      ctx.arc(sx, sy, r, 0, Math.PI * 2);
      ctx.fill();

      if (p.hasShips) {
        ctx.fillStyle = COLORS.ship;
        ctx.beginPath();
        ctx.arc(sx + r, sy - r, 2, 0, Math.PI * 2);
        ctx.fill();
      }

      // Label (only if zoomed in enough)
      if (this.scale > 1.5) {
        ctx.fillStyle  = COLORS.text;
        ctx.font       = '9px monospace';
        ctx.fillText(p.name, sx + r + 2, sy + 3);
      }
    }
  }

  private toScreen(worldDist: number): number { return worldDist * this.scale; }

  private worldToScreen(wx: number, wy: number): [number, number] {
    return [this.panX + wx * this.scale, this.panY + wy * this.scale];
  }

  private screenToWorld(sx: number, sy: number): [number, number] {
    return [(sx - this.panX) / this.scale, (sy - this.panY) / this.scale];
  }

  private setupEvents(): void {
    const el = this.canvas;

    el.addEventListener('wheel', e => {
      e.preventDefault();
      const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
      const rect   = el.getBoundingClientRect();
      const cx     = e.clientX - rect.left;
      const cy     = e.clientY - rect.top;
      // Zoom around cursor
      this.panX = cx - (cx - this.panX) * factor;
      this.panY = cy - (cy - this.panY) * factor;
      this.scale *= factor;
      this.draw();
    }, { passive: false });

    el.addEventListener('mousedown', e => {
      this.dragging  = true;
      this.dragStart = { x: e.clientX, y: e.clientY, panX: this.panX, panY: this.panY };
    });

    el.addEventListener('mousemove', e => {
      if (!this.dragging) return;
      this.panX = this.dragStart.panX + (e.clientX - this.dragStart.x);
      this.panY = this.dragStart.panY + (e.clientY - this.dragStart.y);
      this.draw();
    });

    el.addEventListener('mouseup', e => {
      const dx = Math.abs(e.clientX - this.dragStart.x);
      const dy = Math.abs(e.clientY - this.dragStart.y);
      this.dragging = false;
      if (dx < 4 && dy < 4) this.handleClick(e);
    });

    el.addEventListener('mouseleave', () => { this.dragging = false; });
  }

  private handleClick(e: MouseEvent): void {
    const rect  = this.canvas.getBoundingClientRect();
    const sx    = e.clientX - rect.left;
    const sy    = e.clientY - rect.top;
    const [wx, wy] = this.screenToWorld(sx, sy);

    let closest: MapPlanet | null = null;
    let minDist = Infinity;
    for (const p of this.planets) {
      const d = Math.hypot(p.x - wx, p.y - wy);
      if (d < minDist) { minDist = d; closest = p; }
    }

    if (closest && minDist < 10 / this.scale) {
      this.selected = closest.name;
      this.draw();
      this.onPlanetClick?.(closest.name);
    }
  }
}
