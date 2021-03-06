﻿using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace OdataToEntity.Linq2Db
{
    public abstract class OeLinq2DbTable
    {
        public abstract int SaveDeleted(DataConnection dc);
        public abstract int SaveInserted(DataConnection dc);
        public abstract int SaveUpdated(DataConnection dc);
        public abstract void UpdateIdentities(PropertyInfo fkeyProperty, IDictionary<Object, Object> identities);

        public abstract IDictionary<Object, Object> Identities { get; }
        public PropertyInfo SelfRefProperty { get; set; }
    }

    public sealed class OeLinq2DbTable<T> : OeLinq2DbTable
    {
        private readonly List<T> _deleted;
        private readonly Dictionary<Object, Object> _identities;
        private readonly List<T> _inserted;
        private readonly List<T> _updated;

        public OeLinq2DbTable()
        {
            _deleted = new List<T>();
            _inserted = new List<T>();
            _identities = new Dictionary<Object, Object>();
            _updated = new List<T>();
        }
        public void Delete(T entity)
        {
            _deleted.Add(entity);
        }
        private static List<PropertyInfo> GetDatabaseGenerated()
        {
            var identityProperties = new List<PropertyInfo>();
            foreach (PropertyInfo property in typeof(T).GetProperties())
                if (property.GetCustomAttribute(typeof(IdentityAttribute)) != null)
                    identityProperties.Add(property);
            return identityProperties;
        }
        public void Insert(T entity)
        {
            _inserted.Add(entity);
        }
        private static void OrderBy(PropertyInfo selfRefProperty, PropertyInfo keyProperty, List<T> items)
        {
            if (selfRefProperty == null || items.Count == 0)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                Object parentKey = selfRefProperty.GetValue(items[i]);
                if (parentKey == null)
                    continue;

                for (int j = i; j < items.Count; j++)
                {
                    bool found = false;
                    for (int k = j; k < items.Count; k++)
                    {
                        Object key = keyProperty.GetValue(items[k]);
                        if (key.Equals(parentKey))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        T temp = items[i];
                        items[i] = items[j];
                        items[j] = temp;
                        break;
                    }
                }
            }
        }
        public override int SaveDeleted(DataConnection dc)
        {
            if (base.SelfRefProperty == null)
            {
                foreach (T entity in Deleted)
                    dc.Delete(entity);
                return Deleted.Count;
            }

            List<PropertyInfo> identityProperties = GetDatabaseGenerated();
            OrderBy(base.SelfRefProperty, identityProperties[0], _deleted);
            for (int i = _deleted.Count - 1; i >= 0; i--)
                dc.Delete(_deleted[i]);
            return Deleted.Count;
        }
        public override int SaveInserted(DataConnection dc)
        {
            List<PropertyInfo> identityProperties = GetDatabaseGenerated();
            if (identityProperties.Count == 0)
            {
                foreach (T entity in Inserted)
                    dc.Insert(entity);
                return Inserted.Count;
            }

            OrderBy(base.SelfRefProperty, identityProperties[0], _inserted);
            for(int i = 0; i < _inserted.Count; i++)
            {
                T entity = _inserted[i];
                Object identity = dc.InsertWithIdentity<T>(entity);
                identity = Convert.ChangeType(identity, identityProperties[0].PropertyType);
                Object old = identityProperties[0].GetValue(entity);
                identityProperties[0].SetValue(entity, identity);
                _identities.Add(old, identity);

                if (base.SelfRefProperty != null)
                    UpdateParentIdentity(old, identity, i);
            }
            return Inserted.Count;
        }
        public override int SaveUpdated(DataConnection dc)
        {
            foreach (T entity in Updated)
                dc.Update(entity);
            return Updated.Count;
        }
        public void Update(T entity)
        {
            _updated.Add(entity);
        }
        private void UpdateParentIdentity(Object oldIdentity, Object newIdentity, int entityIndex)
        {
            if (newIdentity.Equals(oldIdentity))
                return;

            for (int i = entityIndex + 1; i < _inserted.Count; i++)
            {
                Object parentIdentity = base.SelfRefProperty.GetValue(_inserted[i]);
                if (oldIdentity.Equals(parentIdentity))
                    base.SelfRefProperty.SetValue(_inserted[i], newIdentity);
            }
        }
        public override void UpdateIdentities(PropertyInfo fkeyProperty, IDictionary<Object, Object> identities)
        {
            foreach (T entity in Inserted)
            {
                Object oldValue = fkeyProperty.GetValue(entity);
                if (oldValue != null)
                {
                    Object newValue;
                    if (identities.TryGetValue(oldValue, out newValue))
                        fkeyProperty.SetValue(entity, newValue);
                }
            }
        }

        public IReadOnlyList<T> Deleted => _deleted;
        public override IDictionary<Object, Object> Identities => _identities;
        public IReadOnlyList<T> Inserted => _inserted;
        public IReadOnlyList<T> Updated => _updated;
    }
}
