﻿using Serenity.Abstractions;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Claims;

namespace Serenity.Services
{
    public class DeleteRequestHandler<TRow, TDeleteRequest, TDeleteResponse> : IDeleteRequestProcessor,
        IDeleteHandler<TRow, TDeleteRequest, TDeleteResponse>
        where TRow : class, IRow, IIdRow, new()
        where TDeleteRequest : DeleteRequest
        where TDeleteResponse : DeleteResponse, new()
    {
        protected TRow Row;
        protected TDeleteResponse Response;
        protected TDeleteRequest Request;
        protected Lazy<IDeleteBehavior[]> behaviors;

        public DeleteRequestHandler(IRequestContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            StateBag = new Dictionary<string, object>();
            behaviors = new Lazy<IDeleteBehavior[]>(() => GetBehaviors().ToArray());
        }

        protected virtual IEnumerable<IDeleteBehavior> GetBehaviors()
        {
            return Context.Behaviors.Resolve<TRow, IDeleteBehavior>(GetType());
        }

        public IDbConnection Connection => UnitOfWork.Connection;

        protected virtual void OnBeforeDelete()
        {
            foreach (var behavior in behaviors.Value)
                behavior.OnBeforeDelete(this);
        }

        protected virtual BaseCriteria GetDisplayOrderFilter()
        {
            return DisplayOrderFilterHelper.GetDisplayOrderFilterFor(Row);
        }

        protected virtual void OnAfterDelete()
        {
            if (Row as IDisplayOrderRow != null)
            {
                var filter = GetDisplayOrderFilter();
                DisplayOrderHelper.ReorderValues(Connection, Row as IDisplayOrderRow, filter, -1, 1, false);
            }

            foreach (var behavior in behaviors.Value)
                behavior.OnAfterDelete(this);
        }

        protected virtual void ValidateRequest()
        {
            foreach (var behavior in behaviors.Value)
                behavior.OnValidateRequest(this);
        }

        protected virtual void PrepareQuery(SqlQuery query)
        {
            query.SelectTableFields();

            foreach (var behavior in behaviors.Value)
                behavior.OnPrepareQuery(this, query);
        }

        protected virtual void LoadEntity()
        {
            var idField = Row.IdField;
            var id = idField.ConvertValue(Request.EntityId, CultureInfo.InvariantCulture);

            var query = new SqlQuery()
                .Dialect(Connection.GetDialect())
                .From(Row)
                .WhereEqual(idField, id);

            PrepareQuery(query);

            if (!query.GetFirst(Connection))
                throw DataValidation.EntityNotFoundError(Row, Request.EntityId, Localizer);
        }

        protected virtual void InvokeDeleteAction(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                foreach (var behavior in behaviors.Value.OfType<IDeleteExceptionBehavior>())
                    behavior.OnException(this, exception);

                throw;
            }
        }

        protected virtual void ExecuteDelete()
        {
            var isActiveDeletedRow = Row as IIsActiveDeletedRow;
            var isDeletedRow = Row as IIsDeletedRow;
            var deleteLogRow = Row as IDeleteLogRow;
            var idField = Row.IdField;
            var id = idField.ConvertValue(Request.EntityId, CultureInfo.InvariantCulture);

            if (isActiveDeletedRow == null && isDeletedRow == null && deleteLogRow == null)
            {
                var delete = new SqlDelete(Row.Table)
                    .WhereEqual(idField, id);

                InvokeDeleteAction(() =>
                {
                    if (delete.Execute(Connection) != 1)
                        throw DataValidation.EntityNotFoundError(Row, id, Localizer);
                });
            }
            else
            {
                if (isDeletedRow != null || isActiveDeletedRow != null)
                {
                    var update = new SqlUpdate(Row.Table)
                        .WhereEqual(idField, id)
                        .Where(ServiceQueryHelper.GetNotDeletedCriteria(Row));

                    if (isActiveDeletedRow != null)
                    {
                        update.Set(isActiveDeletedRow.IsActiveField, -1);
                    }
                    else if (isDeletedRow != null)
                    {
                        update.Set(isDeletedRow.IsDeletedField, true);
                    }

                    if (deleteLogRow != null)
                    {
                        update.Set(deleteLogRow.DeleteDateField, DateTimeField.ToDateTimeKind(DateTime.Now,
                                        deleteLogRow.DeleteDateField.DateTimeKind))
                              .Set(deleteLogRow.DeleteUserIdField, User?.GetIdentifier().TryParseID());
                    }
                    else if (Row is IUpdateLogRow updateLogRow)
                    {
                        update.Set(updateLogRow.UpdateDateField, DateTimeField.ToDateTimeKind(DateTime.Now,
                                        updateLogRow.UpdateDateField.DateTimeKind))
                              .Set(updateLogRow.UpdateUserIdField, User?.GetIdentifier().TryParseID());
                    }

                    InvokeDeleteAction(() =>
                    {
                        if (update.Execute(Connection) != 1)
                            throw DataValidation.EntityNotFoundError(Row, id, Localizer);
                    });
                }
                else //if (deleteLogRow != null)
                {
                    var update = new SqlUpdate(Row.Table)
                        .Set(deleteLogRow.DeleteDateField, DateTimeField.ToDateTimeKind(DateTime.Now,
                                    deleteLogRow.DeleteDateField.DateTimeKind))
                        .Set(deleteLogRow.DeleteUserIdField, User?.GetIdentifier().TryParseID())
                        .WhereEqual(idField, id)
                        .Where(new Criteria(deleteLogRow.DeleteUserIdField).IsNull());

                    InvokeDeleteAction(() =>
                    {
                        if (update.Execute(Connection) != 1)
                            throw DataValidation.EntityNotFoundError(Row, id, Localizer);
                    });
                }
            }

            InvalidateCacheOnCommit();
        }

        protected virtual void InvalidateCacheOnCommit()
        {
            Cache.InvalidateOnCommit(UnitOfWork, Row);
        }

        protected virtual void DoAudit()
        {
            foreach (var behavior in behaviors.Value)
                behavior.OnAudit(this);
        }

        protected virtual void OnReturn()
        {
            foreach (var behavior in behaviors.Value)
                behavior.OnReturn(this);
        }

        protected virtual void ValidatePermissions()
        {
            var attr = typeof(TRow).GetCustomAttribute<DeletePermissionAttribute>(true) ??
                (PermissionAttributeBase)typeof(TRow).GetCustomAttribute<ModifyPermissionAttribute>(true) ??
                typeof(TRow).GetCustomAttribute<ReadPermissionAttribute>(true);

            if (attr != null)
                Permissions.ValidatePermission(attr.Permission ?? "?", Localizer);
        }

        public TDeleteResponse Process(IUnitOfWork unitOfWork, TDeleteRequest request)
        {
            StateBag.Clear();
            UnitOfWork = unitOfWork ?? throw new ArgumentNullException("unitOfWork");
            Request = request;
            Response = new TDeleteResponse();

            if (request.EntityId == null)
                throw DataValidation.RequiredError(nameof(request.EntityId), Localizer);

            Row = new TRow();

            LoadEntity();
            ValidatePermissions();
            ValidateRequest();

            var isActiveDeletedRow = Row as IIsActiveDeletedRow;
            var isDeletedRow = Row as IIsDeletedRow;
            var deleteLogRow = Row as IDeleteLogRow;

            if ((isDeletedRow != null && isDeletedRow.IsDeletedField[Row] == true) ||
                (isActiveDeletedRow != null && isActiveDeletedRow.IsActiveField[Row] < 0) ||
                (deleteLogRow != null && !deleteLogRow.DeleteUserIdField.IsNull(Row)))
                Response.WasAlreadyDeleted = true;
            else
            {
                OnBeforeDelete();

                ExecuteDelete();

                OnAfterDelete();

                DoAudit();
            }

            OnReturn();

            return Response;
        }

        DeleteResponse IDeleteRequestProcessor.Process(IUnitOfWork uow, DeleteRequest request)
        {
            return Process(uow, (TDeleteRequest)request);
        }

        public TDeleteResponse Delete(IUnitOfWork uow, TDeleteRequest request)
        {
            return Process(uow, request);
        }

        public ITwoLevelCache Cache => Context.Cache;
        public IRequestContext Context { get; private set; }
        public ITextLocalizer Localizer => Context.Localizer;
        public IPermissionService Permissions => Context.Permissions;
        public ClaimsPrincipal User => Context.User;

        public IUnitOfWork UnitOfWork { get; protected set; }
        IRow IDeleteRequestHandler.Row => Row;
        DeleteRequest IDeleteRequestHandler.Request => Request;
        DeleteResponse IDeleteRequestHandler.Response => Response;
        public IDictionary<string, object> StateBag { get; private set; }
    }
}