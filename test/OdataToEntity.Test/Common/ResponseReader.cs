﻿using Microsoft.OData;
using Microsoft.OData.Edm;
using Newtonsoft.Json;
using OdataToEntity.Parsers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OdataToEntity.Test
{
    public class ResponseReader
    {
        protected struct NavigationPorperty
        {
            public readonly String Name;
            public readonly ODataResourceSetBase ResourceSet;
            public readonly Object Value;

            public NavigationPorperty(String name, Object value, ODataResourceSetBase resourceSet)
            {
                Name = name;
                Value = value;
                ResourceSet = resourceSet;
            }
        }

        private sealed class StackItem
        {
            private readonly ODataItem _item;
            private readonly List<NavigationPorperty> _navigationProperties;
            private Object _value;

            public StackItem(ODataItem item)
            {
                _item = item;
                _navigationProperties = new List<NavigationPorperty>();
            }

            public void AddEntry(Object value)
            {
                if (Item is ODataNestedResourceInfo link)
                {
                    if (link.IsCollection.GetValueOrDefault())
                    {
                        var list = value as IList;
                        if (list == null)
                            AddToList((dynamic)value);
                        else
                            foreach (Object item in list)
                                AddToList((dynamic)item);
                    }
                    else
                        _value = value;
                    return;
                }

                if (Item is ODataResourceSet set)
                {
                    AddToList((dynamic)value);
                    return;
                }

                throw new NotSupportedException(Item.GetType().ToString());
            }
            public void AddLink(ODataNestedResourceInfo link, Object value, ODataResourceSetBase resourceSet)
            {
                _navigationProperties.Add(new NavigationPorperty(link.Name, value, resourceSet));
            }
            private void AddToList<T>(T value)
            {
                if (Value == null)
                    _value = new List<T>();
                (Value as List<T>).Add(value);
            }

            public ODataItem Item => _item;
            public Object Value => _value;
            public IReadOnlyList<NavigationPorperty> NavigationProperties => _navigationProperties;
            public ODataResourceSetBase ResourceSet { get; set; }
        }

        private readonly IEdmModel _edmModel;
        private readonly Db.OeEntitySetMetaAdapterCollection _entitySetMetaAdapters;
        private readonly Dictionary<IEnumerable, ODataResourceSetBase> _navigationProperties;
        private readonly Dictionary<Object, Dictionary<PropertyInfo, ODataResourceSetBase>> _navigationPropertyEntities;
        private static readonly Dictionary<PropertyInfo, ODataResourceSetBase> EmptyNavigationPropertyEntities = new Dictionary<PropertyInfo, ODataResourceSetBase>();

        public ResponseReader(IEdmModel edmModel, Db.OeEntitySetMetaAdapterCollection entitySetMetaAdapters)
        {
            _edmModel = edmModel;
            _entitySetMetaAdapters = entitySetMetaAdapters;
            _navigationProperties = new Dictionary<IEnumerable, ODataResourceSetBase>();
            _navigationPropertyEntities = new Dictionary<Object, Dictionary<PropertyInfo, ODataResourceSetBase>>();
        }

        protected virtual void AddItems(Object entity, PropertyInfo propertyInfo, IEnumerable values)
        {
            var collection = propertyInfo.GetValue(entity);
            if (collection == null)
            {
                collection = CreateCollection(propertyInfo.PropertyType);
                propertyInfo.SetValue(entity, collection);
            }

            foreach (dynamic value in values)
                ((dynamic)collection).Add(value);
        }
        protected static IEnumerable CreateCollection(Type type)
        {
            Type itemType = OeExpressionHelper.GetCollectionItemType(type);
            return (IEnumerable)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
        }
        protected Object CreateEntity(ODataResource resource, IReadOnlyList<NavigationPorperty> navigationProperties)
        {
            Db.OeEntitySetMetaAdapter entitySetMetaAdapter = EntitySetMetaAdapters.FindByTypeName(resource.TypeName);
            Object entity = OeEntityItem.CreateEntity(entitySetMetaAdapter.EntityType, resource);
            Dictionary<PropertyInfo, ODataResourceSetBase> propertyInfos = null;

            foreach (NavigationPorperty navigationProperty in navigationProperties)
            {
                PropertyInfo clrProperty = entitySetMetaAdapter.EntityType.GetProperty(navigationProperty.Name);
                Object value = navigationProperty.Value;

                if (navigationProperty.ResourceSet == null || (navigationProperty.ResourceSet.Count == null && navigationProperty.ResourceSet.NextPageLink == null))
                {
                    clrProperty.SetValue(entity, value);
                    continue;
                }

                if (value == null && navigationProperty.ResourceSet.NextPageLink != null)
                    value = CreateCollection(clrProperty.PropertyType);

                clrProperty.SetValue(entity, value);
                if (value is IEnumerable collection)
                {
                    NavigationProperties.Add(collection, navigationProperty.ResourceSet);

                    if (propertyInfos == null)
                    {
                        propertyInfos = new Dictionary<PropertyInfo, ODataResourceSetBase>(navigationProperties.Count);
                        NavigationPropertyEntities.Add(entity, propertyInfos);
                    }
                    propertyInfos.Add(clrProperty, navigationProperty.ResourceSet);
                }
            }

            return entity;
        }
        protected virtual Object CreateRootEntity(ODataResource resource, IReadOnlyList<NavigationPorperty> navigationProperties, Type entityType)
        {
            return CreateEntity(resource, navigationProperties);
        }
        public async Task FillNextLinkProperties(OeParser parser, Object entity, CancellationToken token)
        {
            using (var response = new MemoryStream())
                foreach (KeyValuePair<PropertyInfo, ODataResourceSetBase> propertyResourceSet in GetResourceSets(entity))
                {
                    response.SetLength(0);
                    await parser.ExecuteGetAsync(propertyResourceSet.Value.NextPageLink, OeRequestHeaders.JsonDefault, response, token).ConfigureAwait(false);
                    response.Position = 0;

                    var navigationPropertyReader = new ResponseReader(parser.Model, parser.DataAdapter.EntitySetMetaAdapters);
                    AddItems(entity, propertyResourceSet.Key, navigationPropertyReader.Read(response));
                }
        }
        protected static String GetEntitSetName(Stream response)
        {
            using (var streamReader = new StreamReader(response, Encoding.UTF8, false, 1024, true))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                while (jsonReader.Read())
                    if (jsonReader.TokenType == JsonToken.PropertyName && (String)jsonReader.Value == "@odata.context")
                    {
                        if (jsonReader.Read())
                        {
                            var contextUri = new Uri((String)jsonReader.Value, UriKind.Absolute);
                            if (contextUri.Fragment[0] == '#')
                            {
                                int i = contextUri.Fragment.IndexOf('(');
                                if (i == -1)
                                    return contextUri.Fragment.Substring(1);
                                else
                                    return contextUri.Fragment.Substring(1, i - 1);
                            }
                        }
                        return null;
                    }
            }

            return null;
        }
        public ODataResourceSetBase GetResourceSet(IEnumerable navigationProperty)
        {
            return NavigationProperties[navigationProperty];
        }
        public IReadOnlyDictionary<PropertyInfo, ODataResourceSetBase> GetResourceSets(Object entity)
        {
            if (_navigationPropertyEntities.TryGetValue(entity, out Dictionary<PropertyInfo, ODataResourceSetBase> resourceSets))
                return resourceSets;
            return EmptyNavigationPropertyEntities;
        }
        public virtual IEnumerable Read(Stream response)
        {
            String entitySetName = GetEntitSetName(response);
            response.Position = 0;
            Db.OeEntitySetMetaAdapter entitySetMetaAdatpter = EntitySetMetaAdapters.FindByEntitySetName(entitySetName);
            return ReadImpl(response, entitySetMetaAdatpter);
        }
        public IEnumerable<T> Read<T>(Stream response)
        {
            Db.OeEntitySetMetaAdapter entitySetMetaAdatpter = EntitySetMetaAdapters.FindByClrType(typeof(T));
            return ReadImpl(response, entitySetMetaAdatpter).Cast<T>();
        }
        protected IEnumerable ReadImpl(Stream response, Db.OeEntitySetMetaAdapter entitySetMetaAdatpter)
        {
            ResourceSet = null;
            NavigationProperties.Clear();
            NavigationPropertyEntities.Clear();

            IODataResponseMessage responseMessage = new OeInMemoryMessage(response, null);
            var settings = new ODataMessageReaderSettings() { EnableMessageStreamDisposal = false, Validations = ValidationKinds.None };
            var messageReader = new ODataMessageReader(responseMessage, settings, EdmModel);

            IEdmEntitySet entitySet = EdmModel.EntityContainer.FindEntitySet(entitySetMetaAdatpter.EntitySetName);
            ODataReader reader = messageReader.CreateODataResourceSetReader(entitySet, entitySet.EntityType());

            var stack = new Stack<StackItem>();
            while (reader.Read())
            {
                switch (reader.State)
                {
                    case ODataReaderState.ResourceSetStart:
                        if (stack.Count == 0)
                            ResourceSet = (ODataResourceSetBase)reader.Item;
                        else
                            stack.Peek().ResourceSet = (ODataResourceSetBase)reader.Item;
                        break;
                    case ODataReaderState.ResourceStart:
                        stack.Push(new StackItem((ODataResource)reader.Item));
                        break;
                    case ODataReaderState.ResourceEnd:
                        StackItem stackItem = stack.Pop();

                        if (reader.Item != null)
                            if (stack.Count == 0)
                                yield return CreateRootEntity((ODataResource)stackItem.Item, stackItem.NavigationProperties, entitySetMetaAdatpter.EntityType);
                            else
                                stack.Peek().AddEntry(CreateEntity((ODataResource)stackItem.Item, stackItem.NavigationProperties));
                        break;
                    case ODataReaderState.NestedResourceInfoStart:
                        stack.Push(new StackItem((ODataNestedResourceInfo)reader.Item));
                        break;
                    case ODataReaderState.NestedResourceInfoEnd:
                        StackItem item = stack.Pop();
                        stack.Peek().AddLink((ODataNestedResourceInfo)item.Item, item.Value, item.ResourceSet);
                        break;
                }
            }
        }

        protected IEdmModel EdmModel => _edmModel;
        protected Db.OeEntitySetMetaAdapterCollection EntitySetMetaAdapters => _entitySetMetaAdapters;
        protected Dictionary<IEnumerable, ODataResourceSetBase> NavigationProperties => _navigationProperties;
        public Dictionary<Object, Dictionary<PropertyInfo, ODataResourceSetBase>> NavigationPropertyEntities => _navigationPropertyEntities;
        public ODataResourceSetBase ResourceSet { get; protected set; }
    }
}
