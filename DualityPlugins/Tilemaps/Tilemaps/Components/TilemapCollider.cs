﻿using System;
using System.Collections.Generic;
using System.Linq;

using Duality;
using Duality.Editor;
using Duality.Components;
using Duality.Components.Physics;
using Duality.Plugins.Tilemaps.Properties;

namespace Duality.Plugins.Tilemaps
{
	/// <summary>
	/// Uses the information from the local <see cref="Tilemap"/> to configure the local <see cref="RigidBody"/> for 
	/// simulating physical interaction with the <see cref="Tilemap"/>.
	/// </summary>
	[RequiredComponent(typeof(RigidBody))]
	[EditorHintCategory(TilemapsResNames.CategoryTilemaps)]
	[EditorHintImage(TilemapsResNames.ImageTilemapCollider)]
	public class TilemapCollider : Component, ICmpInitializable
	{
		private struct Sector
		{
			public List<ShapeInfo> Shapes;
			public int Checksum;
		}

		private static readonly int SectorSize = 32;
		private static readonly TilemapCollisionSource[] DefaultSource = new TilemapCollisionSource[] 
		{
			new TilemapCollisionSource
			{
				SourceTilemap = null, 
				Layers = TileCollisionLayer.Layer0
			}
		};

		private TilemapCollisionSource[] source = DefaultSource;

		[DontSerialize] private Tilemap[] sourceTilemaps = null;
		[DontSerialize] private Point2 tileCount = Point2.Zero;
		[DontSerialize] private Point2 sectorCount = Point2.Zero;
		[DontSerialize] private Grid<Sector> sectors = null;
		[DontSerialize] private Grid<TileCollisionShape> tempCollisionData = new Grid<TileCollisionShape>(SectorSize, SectorSize);
		[DontSerialize] private TileEdgeMap tempEdgeMap = new TileEdgeMap(SectorSize + 1, SectorSize + 1);


		/// <summary>
		/// [GET / SET] Specifies which <see cref="Tilemap"/> components and collision layers to use
		/// to generate the collision shape.
		/// </summary>
		public IReadOnlyList<TilemapCollisionSource> CollisionSource
		{
			get { return this.source; }
			set
			{
				if (this.source != value)
				{
					this.UnsubscribeSourceEvents();
					this.source = value.ToArray() ?? DefaultSource;
					this.RetrieveSourceTilemaps();
					this.SubscribeSourceEvents();
				}
			}
		}

		private void ClearRigidBody()
		{
			RigidBody body = this.GameObj.GetComponent<RigidBody>();
			body.BeginUpdateBodyShape();
			for (int y = 0; y < this.sectorCount.Y; y++)
			{
				for (int x = 0; x < this.sectorCount.X; x++)
				{
					Sector sector = this.sectors[x, y];
					if (sector.Shapes != null)
					{
						foreach (ShapeInfo shape in sector.Shapes)
							body.RemoveShape(shape);
						sector.Shapes.Clear();
					}
					this.sectors[x, y] = sector;
				}
			}
			body.EndUpdateBodyShape();
		}
		private void UpdateRigidBody()
		{
			this.tileCount = GetTileCount(this.sourceTilemaps);

			this.sectorCount = new Point2(
				1 + ((this.tileCount.X - 1) / SectorSize),
				1 + ((this.tileCount.Y - 1) / SectorSize));
			this.sectors = new Grid<Sector>(
				this.sectorCount.X, 
				this.sectorCount.Y);

			RigidBody body = this.GameObj.GetComponent<RigidBody>();
			body.BeginUpdateBodyShape();
			for (int y = 0; y < this.sectorCount.Y; y++)
			{
				for (int x = 0; x < this.sectorCount.X; x++)
				{
					this.UpdateRigidBody(body, x, y);
				}
			}
			body.EndUpdateBodyShape();
		}
		private void UpdateRigidBody(RigidBody body, int sectorX, int sectorY)
		{
			Log.Core.Write("GenerateCollisionShapes {0}", new Point2(sectorX, sectorY));

			Sector sector = this.sectors[sectorX, sectorY];

			// Determine collision checksum
			var w = System.Diagnostics.Stopwatch.StartNew();
			int newChecksum = this.MergeCollisionData(sectorX, sectorY, this.tempCollisionData);
			w.Stop();
			Log.Core.Write("Checksum Gen Time: {0} ms", w.Elapsed.TotalMilliseconds);

			// If it differs from our previous value, update collision shapes
			if (sector.Checksum != newChecksum)
			{
				Log.Core.Write("Different CheckSum: {0} --> {1}", sector.Checksum, newChecksum);
				var w2 = System.Diagnostics.Stopwatch.StartNew();

				// Clean up old shapes
				if (sector.Shapes != null)
				{
					foreach (ShapeInfo shape in sector.Shapes)
						body.RemoveShape(shape);
					sector.Shapes.Clear();
				}
				else
				{
					sector.Shapes = new List<ShapeInfo>();
				}

				// Generate new shapes
				{
					// Clear the temporary edge map first
					this.tempEdgeMap.Clear();

					// Populate the edge map with fence and block geometry
					AddFenceCollisionEdges(this.tempCollisionData, this.tempEdgeMap);
					AddBlockCollisionEdges(this.tempCollisionData, this.tempEdgeMap);

					// Now traverse the edge map and gradually create chain / loop 
					// shapes until all edges have been used.
					GenerateCollisionShapes(this.tempEdgeMap, sector.Shapes);

					// Add all the generated shapes to the target body
					foreach (ShapeInfo shape in sector.Shapes)
						body.AddShape(shape);
				}
				sector.Checksum = newChecksum;

				w2.Stop();
				Log.Core.Write("Shape Gen Time: {0} ms", w2.Elapsed.TotalMilliseconds);
			}

			this.sectors[sectorX, sectorY] = sector;
		}
		private void GenerateCollisionShapes(Grid<TileCollisionShape> collisionData, TileEdgeMap edgeMap, IList<ShapeInfo> shapeList)
		{
		}
		private int MergeCollisionData(int sectorX, int sectorY, Grid<TileCollisionShape> target)
		{
			Point2 beginTile = new Point2(sectorX * SectorSize, sectorY * SectorSize);
			Point2 endTile = new Point2((sectorX + 1) * SectorSize, (sectorY + 1) * SectorSize);
			endTile.X = Math.Min(endTile.X, this.tileCount.X);
			endTile.Y = Math.Min(endTile.Y, this.tileCount.Y);

			TileInfo[][] tileData = GetRawTileData(this.sourceTilemaps);

			int checksum = 0;
			for (int y = beginTile.Y; y < endTile.Y; y++)
			{
				for (int x = beginTile.X; x < endTile.X; x++)
				{
					TileCollisionShape mergedCollision = TileCollisionShape.Free;
					for (int i = 0; i < this.sourceTilemaps.Length; i++)
					{
						if (this.sourceTilemaps[i] == null) continue;
						if (tileData[i] == null) continue;

						Tile tile = this.sourceTilemaps[i].Tiles[x, y];
						TileCollisionShape collision = tileData[i][tile.Index].Collision[this.source[i].Layers];
						mergedCollision |= collision;
					}
					target[x - beginTile.X, y - beginTile.Y] = mergedCollision;
					MathF.CombineHashCode(ref checksum, (int)mergedCollision);
				}
			}

			return checksum;
		}
		
		private void RetrieveSourceTilemaps()
		{
			Tilemap localTilemap = this.GameObj.GetComponent<Tilemap>();

			this.sourceTilemaps = new Tilemap[this.source.Length];
			for (int i = 0; i < this.sourceTilemaps.Length; i++)
			{
				this.sourceTilemaps[i] = 
					this.source[i].SourceTilemap ?? 
					localTilemap;
			}
		}
		private void SubscribeSourceEvents()
		{
			EventHandler<TilemapChangedEventArgs> handler = this.SourceTilemap_EventTilemapChanged;
			for (int i = 0; i < this.sourceTilemaps.Length; i++)
			{
				if (this.sourceTilemaps[i] == null) continue;

				// Use the unsubscribe-subscribe pattern to avoid subscribing twice
				this.sourceTilemaps[i].EventTilemapChanged -= handler;
				this.sourceTilemaps[i].EventTilemapChanged += handler;
			}
		}
		private void UnsubscribeSourceEvents()
		{
			EventHandler<TilemapChangedEventArgs> handler = this.SourceTilemap_EventTilemapChanged;
			for (int i = 0; i < this.sourceTilemaps.Length; i++)
			{
				if (this.sourceTilemaps[i] == null) continue;
				this.sourceTilemaps[i].EventTilemapChanged -= handler;
			}
		}

		void ICmpInitializable.OnInit(Component.InitContext context)
		{
			if (context == InitContext.Activate)
			{
				this.RetrieveSourceTilemaps();
				this.UpdateRigidBody();
				this.SubscribeSourceEvents();
			}
			else if (context == InitContext.Saved)
			{
				// Since we're removing all generated bodies in the saving process,
				// we'll have to re-generate them now
				this.UpdateRigidBody();
				// ToDo: Replace this with only a temporary removal so we don't
				// actually have to re-generate everything.
			}
		}
		void ICmpInitializable.OnShutdown(Component.ShutdownContext context)
		{
			if (context == ShutdownContext.Deactivate)
			{
				this.UnsubscribeSourceEvents();
				this.sourceTilemaps = null;
			}
			else if (context == ShutdownContext.Saving)
			{
				// To avoid saving the generated collider redundantly, clear
				// all of the generated shapes before saving
				this.ClearRigidBody();
				// ToDo: Replace this with only a temporary removal so we don't
				// actually have to re-generate everything.
			}
		}

		private void SourceTilemap_EventTilemapChanged(object sender, TilemapChangedEventArgs e)
		{
			Log.Core.Write("TilemapChanged: {0}, [{1}:{2}]", e.Component, e.Pos, e.Size);
			Log.Core.PushIndent();

			// If we resized our tilemap, we'll have to do a full update
			Point2 newTileCount = GetTileCount(this.sourceTilemaps);
			if (newTileCount != this.tileCount)
			{
				Log.Core.Write("Resize from {0} to {1}", this.tileCount, newTileCount);
				this.UpdateRigidBody();
			}
			// Otherwise, only update the sectors that are affected by the change
			else
			{
				Point2 minSector = new Point2(
					MathF.Clamp(e.Pos.X / SectorSize, 0, this.sectorCount.X),
					MathF.Clamp(e.Pos.Y / SectorSize, 0, this.sectorCount.Y));
				Point2 maxSector = new Point2(
					MathF.Clamp(1 + (e.Pos.X + e.Size.X) / SectorSize, 0, this.sectorCount.X),
					MathF.Clamp(1 + (e.Pos.Y + e.Size.Y) / SectorSize, 0, this.sectorCount.Y));
				Log.Core.Write("Selective Update of Sectors {0} [inclusive] to {1} [exclusive]", minSector, maxSector);
				Log.Core.PushIndent();
				RigidBody body = this.GameObj.GetComponent<RigidBody>();
				body.BeginUpdateBodyShape();
				for (int y = minSector.Y; y < maxSector.Y; y++)
				{
					for (int x = minSector.X; x < maxSector.X; x++)
					{
						this.UpdateRigidBody(body, x, y);
					}
				}
				body.EndUpdateBodyShape();
				Log.Core.PopIndent();
			}

			Log.Core.PopIndent();
		}

		private static TileInfo[][] GetRawTileData(Tilemap[] tilemaps)
		{
			TileInfo[][] tileData = new TileInfo[tilemaps.Length][];
			for (int i = 0; i < tilemaps.Length; i++)
			{
				if (tilemaps[i] == null) continue;
				if (tilemaps[i].Tileset == null) continue;
				tileData[i] = tilemaps[i].Tileset.Res.TileData.Data;
			}
			return tileData;
		}
		private static Point2 GetTileCount(Tilemap[] tilemaps)
		{
			Point2 count = new Point2(int.MaxValue, int.MaxValue);
			for (int i = 0; i < tilemaps.Length; i++)
			{
				if (tilemaps[i] == null) continue;
				count.X = Math.Min(count.X, tilemaps[i].TileCount.X);
				count.Y = Math.Min(count.Y, tilemaps[i].TileCount.Y);
			}
			return count;
		}

		private static void AddFenceCollisionEdges(Grid<TileCollisionShape> collisionData, TileEdgeMap targetEdgeMap)
		{
			// Populate the edge map with all the collision fences
			for (int y = 0; y < SectorSize; y++)
			{
				for (int x = 0; x < SectorSize; x++)
				{
					TileCollisionShape collision = collisionData[x, y];

					// Skip both free and completely solid tiles
					if (collision == TileCollisionShape.Free)
						continue;
					if ((collision & TileCollisionShape.Solid) == TileCollisionShape.Solid)
						continue;

					// Add the various fence collision types
					if ((collision & TileCollisionShape.Top) != TileCollisionShape.Free)
						targetEdgeMap.AddEdge(new Point2(x, y), new Point2(x + 1, y));
					if ((collision & TileCollisionShape.Bottom) != TileCollisionShape.Free)
						targetEdgeMap.AddEdge(new Point2(x, y + 1), new Point2(x + 1, y + 1));
					if ((collision & TileCollisionShape.Left) != TileCollisionShape.Free)
						targetEdgeMap.AddEdge(new Point2(x, y), new Point2(x, y + 1));
					if ((collision & TileCollisionShape.Right) != TileCollisionShape.Free)
						targetEdgeMap.AddEdge(new Point2(x + 1, y), new Point2(x + 1, y + 1));
					if ((collision & TileCollisionShape.DiagonalDown) != TileCollisionShape.Free)
						targetEdgeMap.AddEdge(new Point2(x, y), new Point2(x + 1, y + 1));
					if ((collision & TileCollisionShape.DiagonalUp) != TileCollisionShape.Free)
						targetEdgeMap.AddEdge(new Point2(x, y + 1), new Point2(x + 1, y));
				}
			}
		}
		private static void AddBlockCollisionEdges(Grid<TileCollisionShape> collisionData, TileEdgeMap targetEdgeMap)
		{
			// Add block geometry to the specified edge map
			for (int y = 0; y < SectorSize; y++)
			{
				for (int x = 0; x < SectorSize; x++)
				{
					// Skip non-solid blocks
					bool center = (collisionData[x, y] & TileCollisionShape.Solid) == TileCollisionShape.Solid;
					if (!center) continue;

					// A filled block will always overwrite its inner diagonal edges
					targetEdgeMap.RemoveEdge(new Point2(x, y), new Point2(x + 1, y + 1));
					targetEdgeMap.RemoveEdge(new Point2(x, y + 1), new Point2(x + 1, y));

					// Determine block collision neighbourhood
					bool left   = (x == 0)              || (collisionData[x - 1, y] & TileCollisionShape.Solid) == TileCollisionShape.Solid;
					bool right  = (x == SectorSize - 1) || (collisionData[x + 1, y] & TileCollisionShape.Solid) == TileCollisionShape.Solid;
					bool top    = (y == 0)              || (collisionData[x, y - 1] & TileCollisionShape.Solid) == TileCollisionShape.Solid;
					bool bottom = (y == SectorSize - 1) || (collisionData[x, y + 1] & TileCollisionShape.Solid) == TileCollisionShape.Solid;

					// Adjust outer edge states 
					if (center != left )  targetEdgeMap.AddEdge   (new Point2(x, y), new Point2(x, y + 1));
					else                  targetEdgeMap.RemoveEdge(new Point2(x, y), new Point2(x, y + 1));
					if (center != right)  targetEdgeMap.AddEdge   (new Point2(x + 1, y), new Point2(x + 1, y + 1));
					else                  targetEdgeMap.RemoveEdge(new Point2(x + 1, y), new Point2(x + 1, y + 1));
					if (center != top)    targetEdgeMap.AddEdge   (new Point2(x, y), new Point2(x + 1, y));
					else                  targetEdgeMap.RemoveEdge(new Point2(x, y), new Point2(x + 1, y));
					if (center != bottom) targetEdgeMap.AddEdge   (new Point2(x, y + 1), new Point2(x + 1, y + 1));
					else                  targetEdgeMap.RemoveEdge(new Point2(x, y + 1), new Point2(x + 1, y + 1));
				}
			}
		}
		private static void GenerateCollisionShapes(TileEdgeMap edgeMap, IList<ShapeInfo> shapeList)
		{
			// Traverse the edge map and gradually create chain / loop 
			// shapes until all edges have been used.
			RawList<Point2> currentChain = new RawList<Point2>();
			RawList<Vector2> vertexBuffer = new RawList<Vector2>();
			while (true)
			{
				// Begin a new continuous chain of nodes
				currentChain.Clear();

				// Find a starting node for our current chain.
				// If there is none, we found and handled all edges.
				Point2 start = edgeMap.FindNonEmpty();
				if (start == new Point2(-1, -1))
					break;

				// Traverse the current chain node-by-node from the start we found
				Point2 current = start;
				while (true)
				{
					// Add the current node to our continuous chain
					currentChain.Add(current);

					// Find the next node that connects to the current one.
					// If there is none, our current chain is done.
					Point2 next = edgeMap.GetClockwiseNextFrom(current);
					if (next == new Point2(-1, -1))
						break;

					// Remove the edge we used to get to the next node
					edgeMap.RemoveEdge(current, next);

					// Use the next node as origin for traversing further
					current = next;
				}

				// Generate a shape from the current chain
				bool isLoop = (start == currentChain[currentChain.Count - 1]);
				vertexBuffer.Clear();
				vertexBuffer.Count = isLoop ? currentChain.Count - 1 : currentChain.Count;
				for (int i = 0; i < vertexBuffer.Count; i++)
				{
					vertexBuffer[i] = 20.0f * (Vector2)currentChain[i];
				}
				shapeList.Add(isLoop ? 
					(ShapeInfo)new LoopShapeInfo(vertexBuffer) : 
					(ShapeInfo)new ChainShapeInfo(vertexBuffer));
			}
		}
	}
}